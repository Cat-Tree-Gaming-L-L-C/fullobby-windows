using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ChllSeeding.MockApi;

/// <summary>A single SSE frame to push to subscribers.</summary>
public sealed record SseMessage(string Event, string Data);

/// <summary>Mutable per-server state the scenarios drive.</summary>
public sealed class ServerState
{
    public required string Region { get; init; }
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
    // threshold → candidate changes → the client transitions (switch countdown / Seed All rotation).
    // Only the seeded server (from the latest start-session) fills, so the next server stays put until
    // the client actually switches to it — no back-to-back transitions.
    public bool AutoAdvanceEnabled { get; set; }
    public int AutoAdvanceStep { get; set; } = 4;
    public int AutoAdvanceIntervalSecs { get; set; } = 4;
    public int DwellTicks { get; set; } = 3;   // beats to hold a freshly-seeded server before it climbs

    private (string Region, int Index)? _seedTarget;
    private int _dwellRemaining;

    public MockState() => ResetToDefaults();

    private sealed record SessionRec(string Id, DateTimeOffset Started)
    {
        public int Count { get; set; }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Defaults ────────────────────────────────────────────────────────────────

    /// <summary>Reset to a clean, useful baseline: two NA + one EU HLL server, all seedable.</summary>
    public void ResetToDefaults()
    {
        lock (_gate)
        {
            _servers.Clear();
            _servers.Add(new ServerState { Region = "na", Index = 0, MapName = "Foy",
                Info = new ServerInfo("10.0.0.1:28015", "Esprit", "Esprit de Corps Gaming", 50, "hll"), PlayerCount = 32 });
            _servers.Add(new ServerState { Region = "na", Index = 1, MapName = "Carentan",
                Info = new ServerInfo("10.0.0.2:28015", "Pathfinders", "Pathfinders Chicago", 50, "hll"), PlayerCount = 14 });
            _servers.Add(new ServerState { Region = "eu", Index = 0, MapName = "Omaha",
                Info = new ServerInfo("10.0.1.1:28015", "EU-One", "EU Seed Server One", 50, "hll"), PlayerCount = 8 });
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

    public ServerState? Find(string region, int index)
    {
        lock (_gate)
        {
            return _servers.FirstOrDefault(s =>
                s.Region.Equals(region, StringComparison.OrdinalIgnoreCase) && s.Index == index);
        }
    }

    // ── Wire payloads ─────────────────────────────────────────────────────────────

    public ServersResponse ServersResponse()
    {
        lock (_gate)
        {
            var na = _servers.Where(s => s.Region == "na").OrderBy(s => s.Index).Select(s => s.Info).ToList();
            var eu = _servers.Where(s => s.Region == "eu").OrderBy(s => s.Index).Select(s => s.Info).ToList();
            return new ServersResponse(new RegionServers(na, eu), null, NowMs());
        }
    }

    public List<BatchStatsResult> Stats()
    {
        lock (_gate)
        {
            return _servers.OrderBy(s => s.Region).ThenBy(s => s.Index).Select(s =>
                new BatchStatsResult(s.Info.Game, s.Region, s.Index, s.MapName,
                    s.PlayerCount, s.MaxPlayerCount, s.Offline, s.PasswordProtected, s.Error)).ToList();
        }
    }

    /// <summary>Best seeding candidate per region: an online, non-passworded server still under its
    /// threshold, preferring the one closest to filling (highest population). Null when none qualify.</summary>
    public SeedingStatusResponse SeedingStatus()
    {
        lock (_gate)
        {
            return new SeedingStatusResponse(new GameSeedingStatus(Candidate("na"), Candidate("eu")), null, NowMs());
        }
    }

    private RegionCandidate? Candidate(string region)
    {
        var best = _servers
            .Where(s => s.Region == region && !s.Offline && !s.PasswordProtected
                && s.PlayerCount is { } pc && pc < s.Info.SeedingThreshold)
            .OrderByDescending(s => s.PlayerCount)
            .FirstOrDefault();
        return best is null ? null : new RegionCandidate(best.Info.Game, best.Index, best.Info);
    }

    // ── Mutators (each broadcasts) ────────────────────────────────────────────────

    public bool SetPlayers(string region, int index, int? players, int? max)
    {
        var s = Find(region, index);
        if (s is null) return false;
        lock (_gate)
        {
            if (players is not null) s.PlayerCount = players;
            if (max is not null) s.MaxPlayerCount = max;
        }
        BroadcastState();
        return true;
    }

    public bool SetFlags(string region, int index, bool? offline, bool? passworded)
    {
        var s = Find(region, index);
        if (s is null) return false;
        lock (_gate)
        {
            if (offline is not null) s.Offline = offline.Value;
            if (passworded is not null) s.PasswordProtected = passworded.Value;
        }
        BroadcastState();
        return true;
    }

    /// <summary>Advance each region's current seeding candidate toward (and over) its threshold,
    /// simulating the server filling as it's seeded. When a candidate crosses its threshold it stops
    /// being a candidate and the next server takes over — which is exactly what makes the client
    /// switch / rotate. Broadcasts the new state.</summary>
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
                var s = _servers.FirstOrDefault(x => x.Region == tgt.Region && x.Index == tgt.Index);
                if (s is { Offline: false, PasswordProtected: false }
                    && s.PlayerCount is { } pc && pc < s.Info.SeedingThreshold)
                {
                    var max = s.MaxPlayerCount ?? 100;
                    var next = pc + Math.Max(1, AutoAdvanceStep);
                    // Climb gradually to the threshold; on the tick that reaches it, fill the rest of
                    // the way (self-sustaining). The full server collapses the client's fill-based
                    // switch stagger to ~0, so the candidate change triggers a prompt switch.
                    s.PlayerCount = next >= s.Info.SeedingThreshold ? max : next;
                    changed = true;
                }
            }
        }
        if (changed) BroadcastState();
    }

    // ── Sessions ──────────────────────────────────────────────────────────────────

    public string StartSession(string region, int index)
    {
        var id = $"sess-{Interlocked.Increment(ref _seq)}";
        _sessions[id] = new SessionRec(id, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            // Auto-advance now fills THIS server. When it's a new target (first seed or a switch),
            // hold it at its starting population for a few beats before climbing.
            if (_seedTarget != (region, index))
            {
                _seedTarget = (region, index);
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
