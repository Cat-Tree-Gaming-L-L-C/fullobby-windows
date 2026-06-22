using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ChllSeeding.MockApi;

/// <summary>A single SSE frame to push to subscribers.</summary>
public sealed record SseMessage(string Event, string Data);

/// <summary>Mutable per-server state the scenarios drive (single ordered rotation; region removed).</summary>
public sealed class ServerState
{
    public required int Index { get; init; }
    public required ServerInfo Info { get; set; }
    public string? MapName { get; set; } = "Foy";
    public int? PlayerCount { get; set; }
    public int? MaxPlayerCount { get; set; } = 100;
    public bool Offline { get; set; }
    public bool PasswordProtected { get; set; }
    public string? Error { get; set; }
}

/// <summary>An armed HTTP error to return on the next <c>Count</c> matching requests.</summary>
public sealed class ArmedError
{
    public required int Status { get; init; }
    public int Count { get; set; }
    public int? RetryAfterSecs { get; init; }
    public string? MinimumVersion { get; init; }
}

/// <summary>In-memory backend state + SSE fan-out. Singleton. Thread-safe; mutators broadcast
/// fresh <c>stats</c> and <c>seeding_status</c> frames to every connected SSE client.</summary>
public sealed class MockState
{
    private readonly object _gate = new();
    private readonly List<ServerState> _servers = new();
    private readonly ConcurrentDictionary<string, SessionRec> _sessions = new();
    private readonly List<Channel<SseMessage>> _subs = new();
    private int _seq;

    public ArmedError? Armed { get; set; }

    // Auto-advance: simulate the server *currently being seeded* filling up over time so it crosses
    // threshold → candidate changes → the client transitions (switch countdown). Only the seeded
    // server (from the latest start-session) fills, so the next server stays put until the client
    // actually switches to it — no back-to-back transitions.
    public bool AutoAdvanceEnabled { get; set; }
    public int AutoAdvanceStep { get; set; } = 4;
    public int AutoAdvanceIntervalSecs { get; set; } = 4;
    public int DwellTicks { get; set; } = 3;   // beats to hold a freshly-seeded server before it climbs

    private int? _seedTarget;
    private int _dwellRemaining;

    public MockState() => ResetToDefaults();

    private sealed record SessionRec(string Id, DateTimeOffset Started)
    {
        public int Count { get; set; }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Defaults ────────────────────────────────────────────────────────────────

    /// <summary>Reset to a clean, useful baseline: three HLL servers in one rotation, all seedable.</summary>
    public void ResetToDefaults()
    {
        lock (_gate)
        {
            _servers.Clear();
            _servers.Add(new ServerState { Index = 0, MapName = "Foy",
                Info = new ServerInfo("10.0.0.1:28015", 1001, "Esprit", "Esprit de Corps Gaming", 50, "hll"), PlayerCount = 32 });
            _servers.Add(new ServerState { Index = 1, MapName = "Carentan",
                Info = new ServerInfo("10.0.0.2:28015", 1002, "Pathfinders", "Pathfinders Chicago", 50, "hll"), PlayerCount = 14 });
            _servers.Add(new ServerState { Index = 2, MapName = "Omaha",
                Info = new ServerInfo("10.0.1.1:28015", 1003, "Seed-Three", "Seed Server Three", 50, "hll"), PlayerCount = 8 });
            _sessions.Clear();
            Armed = null;
            _seedTarget = null;
            _dwellRemaining = 0;
        }
        BroadcastState();
    }

    public IReadOnlyList<ServerState> Servers
    {
        get { lock (_gate) { return _servers.ToList(); } }
    }

    public ServerState? Find(int index)
    {
        lock (_gate)
        {
            return _servers.FirstOrDefault(s => s.Index == index);
        }
    }

    // ── Wire payloads ─────────────────────────────────────────────────────────────

    public ServersResponse ServersResponse()
    {
        lock (_gate)
        {
            var hll = _servers.OrderBy(s => s.Index).Select(s => s.Info).ToList();
            return new ServersResponse(hll, null, NowMs());
        }
    }

    public List<BatchStatsResult> Stats()
    {
        lock (_gate)
        {
            return _servers.OrderBy(s => s.Index).Select(s =>
                new BatchStatsResult(s.Info.Game, s.Index, s.MapName,
                    s.PlayerCount, s.MaxPlayerCount, s.Offline, s.PasswordProtected, s.Error)).ToList();
        }
    }

    /// <summary>Best seeding candidate: the first online, non-passworded server still under its
    /// threshold (lowest index wins — sequential rotation). Null when none qualify.</summary>
    public SeedingStatusResponse SeedingStatus()
    {
        lock (_gate)
        {
            return new SeedingStatusResponse(Candidate(), null, NowMs());
        }
    }

    private SeedingCandidate? Candidate()
    {
        var best = _servers
            .Where(s => Seedable(s))
            .OrderBy(s => s.Index)
            .FirstOrDefault();
        return best is null ? null : new SeedingCandidate(best.Info.Game, best.Index, best.Info, best.Info.BmId);
    }

    private static bool Seedable(ServerState s) =>
        !s.Offline && !s.PasswordProtected
        && s.PlayerCount is { } pc && pc < s.Info.SeedingThreshold;

    // ── Directive (server-decided rotation) ────────────────────────────────────────

    /// <summary>Default seeding config served to the client (mirrors the Core defaults).</summary>
    public static SeedingConfig DefaultConfig() => new(
        StaggerMaxSecs: 90, StaggerJitterSecs: 15, SwitchCountdownSecs: 30, SnoozeMinSecs: 60, SnoozeMaxSecs: 1800,
        MaxSessionSecs: 18000, MonitorMinSecs: 15, MonitorMaxSecs: 60, MonitorBackoffStepSecs: 15, HeartbeatSecs: 30,
        PollFallbackSecs: 10, PollIdleSecs: 60, DefaultSeedingThreshold: 75, SseKeepaliveSecs: 60, SseBackoffCapSecs: 300,
        CacheStaleSecs: 90, GameOpenRetrySecs: new List<int> { 60, 120 }, GameOpenTimeoutSecs: 180, SplashBypassSecs: 20,
        MissedAutoseedWindowHours: 4, ActiveWindows: new List<TimeWindow>(), DailyResetHourUtc: 10);

    /// <summary>Compute the directive for a polling client: sequential seeding of the first
    /// not-yet-seeded server. <c>stay</c> when the client is already on the target, <c>switch</c>
    /// when a different server should be seeded, <c>stop</c> when all are seeded.</summary>
    public SeedingDirective Directive(int? currentIndex)
    {
        lock (_gate)
        {
            var config = DefaultConfig();
            var target = _servers.Where(Seedable).OrderBy(s => s.Index).FirstOrDefault();

            if (target is null)
            {
                // Nothing left to seed.
                return new SeedingDirective("stop", null, AllExhausted: true, ScheduledPause: false,
                    NextActiveInSecs: null, StaggerSecs: 0, CountdownSecs: config.SwitchCountdownSecs,
                    SnoozeMinSecs: config.SnoozeMinSecs, SnoozeMaxSecs: config.SnoozeMaxSecs,
                    PollAgainInSecs: config.PollIdleSecs, MaxSessionSecs: config.MaxSessionSecs, Config: config);
            }

            var tgt = new DirectiveTarget(target.Info.Game, target.Index, target.Info, target.Info.BmId);

            // Already on the target → keep seeding it.
            var action = currentIndex is { } ci && ci == target.Index ? "stay" : "switch";

            return new SeedingDirective(action, tgt, AllExhausted: false, ScheduledPause: false,
                NextActiveInSecs: null,
                StaggerSecs: action == "switch" ? 0 : 0,
                CountdownSecs: config.SwitchCountdownSecs,
                SnoozeMinSecs: config.SnoozeMinSecs, SnoozeMaxSecs: config.SnoozeMaxSecs,
                PollAgainInSecs: config.MonitorMinSecs, MaxSessionSecs: config.MaxSessionSecs, Config: config);
        }
    }

    // ── Mutators (each broadcasts) ────────────────────────────────────────────────

    public bool SetPlayers(int index, int? players, int? max)
    {
        var s = Find(index);
        if (s is null) return false;
        lock (_gate)
        {
            if (players is not null) s.PlayerCount = players;
            if (max is not null) s.MaxPlayerCount = max;
        }
        BroadcastState();
        return true;
    }

    public bool SetFlags(int index, bool? offline, bool? passworded)
    {
        var s = Find(index);
        if (s is null) return false;
        lock (_gate)
        {
            if (offline is not null) s.Offline = offline.Value;
            if (passworded is not null) s.PasswordProtected = passworded.Value;
        }
        BroadcastState();
        return true;
    }

    /// <summary>Advance the current seeding candidate toward (and over) its threshold, simulating the
    /// server filling as it's seeded. When a candidate crosses its threshold it stops being a candidate
    /// and the next server takes over — which is exactly what makes the client switch / rotate.</summary>
    public void Tick()
    {
        var changed = false;
        lock (_gate)
        {
            // Hold a few beats after switching to a new server before it starts filling.
            if (_dwellRemaining > 0)
            {
                _dwellRemaining--;
            }
            else if (_seedTarget is { } tgt)
            {
                var s = _servers.FirstOrDefault(x => x.Index == tgt);
                if (s is { Offline: false, PasswordProtected: false }
                    && s.PlayerCount is { } pc && pc < s.Info.SeedingThreshold)
                {
                    var max = s.MaxPlayerCount ?? 100;
                    var next = pc + Math.Max(1, AutoAdvanceStep);
                    s.PlayerCount = next >= s.Info.SeedingThreshold ? max : next;
                    changed = true;
                }
            }
        }
        if (changed) BroadcastState();
    }

    // ── Sessions ──────────────────────────────────────────────────────────────────

    public string StartSession(int index)
    {
        var id = $"sess-{Interlocked.Increment(ref _seq)}";
        _sessions[id] = new SessionRec(id, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            // Auto-advance now fills THIS server. When it's a new target (first seed or a switch),
            // hold it at its starting population for a few beats before climbing.
            if (_seedTarget != index)
            {
                _seedTarget = index;
                _dwellRemaining = DwellTicks;
            }
        }
        return id;
    }

    public HeartbeatResponse Heartbeat(string sessionId)
    {
        var rec = _sessions.GetOrAdd(sessionId, id => new SessionRec(id, DateTimeOffset.UtcNow));
        rec.Count++;
        var dur = (long)(DateTimeOffset.UtcNow - rec.Started).TotalSeconds;
        // "validated" after a couple of heartbeats — mirrors the real backend's grace window.
        return new HeartbeatResponse(sessionId, "active", rec.Count, dur, rec.Count >= 2);
    }

    public void StopSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        if (_sessions.IsEmpty)
        {
            lock (_gate) { _seedTarget = null; _dwellRemaining = 0; }
        }
    }

    /// <summary>True while the client has at least one open seeding session — auto-advance only fills
    /// servers while a seed is actually running, so the list stays seedable until you click Seed.</summary>
    public bool HasActiveSession => !_sessions.IsEmpty;

    // ── SSE fan-out ───────────────────────────────────────────────────────────────

    public Channel<SseMessage> Subscribe()
    {
        var ch = Channel.CreateUnbounded<SseMessage>();
        lock (_gate) { _subs.Add(ch); }
        return ch;
    }

    public void Unsubscribe(Channel<SseMessage> ch)
    {
        lock (_gate) { _subs.Remove(ch); }
        ch.Writer.TryComplete();
    }

    public int SubscriberCount { get { lock (_gate) { return _subs.Count; } } }

    /// <summary>Push the current stats + seeding_status to all connected SSE clients.</summary>
    public void BroadcastState()
    {
        Push(new SseMessage("stats", JsonSerializer.Serialize(Stats(), Json.Options)));
        Push(new SseMessage("seeding_status", JsonSerializer.Serialize(SeedingStatus(), Json.Options)));
    }

    public void Push(SseMessage msg)
    {
        lock (_gate)
        {
            foreach (var ch in _subs) ch.Writer.TryWrite(msg);
        }
    }
}

/// <summary>Shared JSON options: snake_case fields, omit nulls — matches the client's ApiJson.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
