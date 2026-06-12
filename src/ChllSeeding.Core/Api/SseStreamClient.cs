using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ChllSeeding.Core.Seeding;
using ChllSeeding.Core.Servers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Api;

/// <summary>
/// Long-running Server-Sent-Events client for the live stats stream
/// (<c>GET /api/servers/stats/stream</c>) — the primary live feed, with the
/// <c>AppBootstrapper</c> HTTP poll as fallback. Parses <c>stats</c> events into
/// the <see cref="ServerStore"/> (via <see cref="LiveStats"/>) and <c>seeding_status</c>
/// events into the <see cref="SeedingStatusCache"/>, and wakes the seeding monitor on
/// (re)connect. Reconnects with exponential backoff + jitter, honors <c>Retry-After</c>
/// on 429, applies a 60s keepalive timeout (with wake-from-sleep handling), and refreshes
/// auth on 401. Port of <c>src-rust/src/api/sse.rs</c>. Runs as an <see cref="IHostedService"/>.
/// </summary>
public sealed class SseStreamClient : IHostedService
{
    /// <summary>Named client: infinite timeout, no auth/resilience handlers (see ServiceCollectionExtensions).</summary>
    public const string ClientName = "sse";

    private const int KeepaliveSecs = 60;
    private const int WakeThresholdSecs = 120;
    private const int ConnectTimeoutSecs = 30;
    private const long BackoffCapSecs = 300;

    private readonly IHttpClientFactory _httpFactory;
    private readonly AuthSession _auth;
    private readonly AuthRefresher _refresher;
    private readonly SeedingStatusCache _statusCache;
    private readonly LiveStats _liveStats;
    private readonly SseConnectionState _state;
    private readonly SeedingEngine _engine;
    private readonly ILogger<SseStreamClient> _log;

    private readonly CancellationTokenSource _lifetime = new();
    // App-code randomness for backoff jitter (not security-sensitive).
    private readonly Random _jitter = new();
    private Task? _loop;

    public SseStreamClient(
        IHttpClientFactory httpFactory,
        AuthSession auth,
        AuthRefresher refresher,
        SeedingStatusCache statusCache,
        LiveStats liveStats,
        SseConnectionState state,
        SeedingEngine engine,
        ILogger<SseStreamClient> log)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _refresher = refresher;
        _statusCache = statusCache;
        _liveStats = liveStats;
        _state = state;
        _engine = engine;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception e) { _log.LogDebug(e, "SSE cancel on shutdown failed"); }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception e) { _log.LogDebug(e, "SSE loop ended with error"); }
        }
        _state.Connected = false;
    }

    // ── Connect/reconnect loop (port of run_sse_loop) ──────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            var outcome = await RunConnectionAsync(failures, ct).ConfigureAwait(false);
            if (outcome.Shutdown)
            {
                break;
            }
            failures = outcome.Failures;

            // Server gave an explicit Retry-After: wait exactly that (we already slept) — skip backoff.
            if (outcome.SkipBackoff)
            {
                continue;
            }

            var backoff = ComputeBackoffSecs(failures);
            if (backoff > 0)
            {
                _state.FailureCount = failures;
                var jitter = _jitter.Next(0, (int)Math.Min(backoff, 3) + 1);
                var wait = backoff + jitter;
                _log.LogInformation("SSE reconnecting in {Wait}s (failure #{Failures})", wait, failures);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _state.Connected = false;
        _log.LogInformation("SSE loop ended");
    }

    private readonly record struct ConnectionOutcome(int Failures, bool Shutdown, bool SkipBackoff);

    private async Task<ConnectionOutcome> RunConnectionAsync(int failures, CancellationToken ct)
    {
        var url = $"{ApiConfig.BaseUrl}/api/servers/stats/stream";
        HttpResponseMessage response;

        // ── Connect (bounded by a short connect timeout, not the infinite stream timeout) ──
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSecs));

            var http = _httpFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/event-stream");
            AuthHeaders.Apply(request, _auth.Token, _auth.ApiKey);

            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ConnectionOutcome(failures, Shutdown: true, SkipBackoff: false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "SSE connect error");
            _state.Connected = false;
            _state.RequestPoll();
            return new ConnectionOutcome(failures + 1, false, false);
        }

        // ── Non-success status (401 refresh / 429 Retry-After / other) ──
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var retryAfter = status == HttpStatusCode.TooManyRequests ? ParseRetryAfterSecs(response) : null;
            string body;
            try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch (Exception) { body = ""; }
            response.Dispose();

            _log.LogWarning("SSE error status: {Status} body={Body}", (int)status,
                body.Length > 128 ? body[..128] : body);
            _state.Connected = false;
            _state.RequestPoll();

            if (status == HttpStatusCode.Unauthorized)
            {
                try { await _refresher.RefreshAsync(_auth.Token, ct).ConfigureAwait(false); }
                catch (Exception e) { _log.LogWarning(e, "SSE auth refresh failed"); }
            }

            if (retryAfter is { } secs)
            {
                _state.FailureCount = failures;
                var jitter = _jitter.Next(1, 4);
                var wait = secs + jitter;
                _log.LogInformation("SSE rate-limited, waiting {Wait}s (Retry-After {Secs}s)", wait, secs);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new ConnectionOutcome(failures, true, false); }
                return new ConnectionOutcome(failures, false, SkipBackoff: true);
            }

            return new ConnectionOutcome(failures + 1, false, false);
        }

        // ── Connected: stream the body ──
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType != "text/event-stream")
        {
            _log.LogWarning("SSE unexpected content-type: {ContentType}", mediaType);
            response.Dispose();
            _state.Connected = false;
            _state.RequestPoll();
            return new ConnectionOutcome(failures + 1, false, false);
        }

        _log.LogInformation("SSE connected");
        _state.Connected = true;
        _state.FailureCount = 0;
        // Re-fetch via HTTP on next monitor tick to catch events lost during the disconnect.
        _statusCache.Invalidate();
        _engine.NotifyMonitor();

        try
        {
            return await ReadStreamAsync(response, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<ConnectionOutcome> ReadStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var parser = new SseFrameParser();
        using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reconnectRequested = false;

        // Watch for a manual reconnect request alongside the read.
        _ = Task.Run(async () =>
        {
            try
            {
                await _state.WaitForReconnect(kaCts.Token).ConfigureAwait(false);
                reconnectRequested = true;
                await kaCts.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* connection ended first */ }
        }, ct);

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var lastActivity = Stopwatch.StartNew();
        kaCts.CancelAfter(TimeSpan.FromSeconds(KeepaliveSecs));

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(kaCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    _state.Connected = false;
                    return new ConnectionOutcome(0, Shutdown: true, false);
                }
                if (reconnectRequested)
                {
                    _log.LogInformation("SSE reconnect requested");
                    _state.Connected = false;
                    // 1s cooldown to avoid rate-limit thrashing; clear the surfaced failure count.
                    _state.FailureCount = 0;
                    return new ConnectionOutcome(1, false, false);
                }
                // Keepalive timeout.
                return await HandleKeepaliveTimeoutAsync(lastActivity.Elapsed, ct).ConfigureAwait(false);
            }

            if (line is null)
            {
                _log.LogInformation("SSE stream ended");
                _state.Connected = false;
                _state.RequestPoll();
                return new ConnectionOutcome(1, false, false);
            }

            // Any line is proof of life — reset the keepalive window.
            lastActivity.Restart();
            kaCts.CancelAfter(TimeSpan.FromSeconds(KeepaliveSecs));

            if (parser.Feed(line) is { } frame)
            {
                Dispatch(frame);
            }
        }
    }

    private async Task<ConnectionOutcome> HandleKeepaliveTimeoutAsync(TimeSpan elapsed, CancellationToken ct)
    {
        _state.Connected = false;
        if (IsWakeFromSleep(elapsed, WakeThresholdSecs))
        {
            // System likely woke from sleep — give the network a moment, then retry without penalty.
            _log.LogInformation("SSE keepalive timeout after wake (elapsed {Secs:F0}s), waiting 3s for network",
                elapsed.TotalSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new ConnectionOutcome(0, Shutdown: true, false); }
            _state.FailureCount = 0;
            return new ConnectionOutcome(0, false, false);
        }
        _log.LogInformation("SSE keepalive timeout ({Secs}s), reconnecting", KeepaliveSecs);
        _state.RequestPoll();
        return new ConnectionOutcome(1, false, false);
    }

    private void Dispatch(SseFrame frame)
    {
        switch (frame.EventType)
        {
            case "stats":
                try
                {
                    var stats = JsonSerializer.Deserialize<List<BatchStatsResult>>(frame.Data, ApiJson.Options);
                    if (stats is not null)
                    {
                        _liveStats.Apply(stats);
                    }
                }
                catch (JsonException e) { _log.LogWarning(e, "SSE stats parse error"); }
                break;
            case "seeding_status":
                try
                {
                    var status = JsonSerializer.Deserialize<SeedingStatusResponse>(frame.Data, ApiJson.Options);
                    if (status is not null)
                    {
                        _statusCache.Update(status);
                    }
                }
                catch (JsonException e) { _log.LogWarning(e, "SSE seeding_status parse error"); }
                break;
            // "heartbeat" and other events are keepalive only — no action.
        }
    }

    // ── Pure helpers (unit-tested) ─────────────────────────────────────────────

    /// <summary>Exponential reconnect backoff: 0 when there were no failures, else
    /// <c>min(2^(failures-1), 300)</c> seconds (jitter added separately).</summary>
    public static long ComputeBackoffSecs(int failures)
    {
        if (failures <= 0)
        {
            return 0;
        }
        var shift = Math.Min(failures - 1, 30);
        return Math.Min(1L << shift, BackoffCapSecs);
    }

    /// <summary>True when an inactivity gap is long enough to imply the system slept (vs a normal
    /// keepalive miss), warranting a network-settle wait instead of a backoff penalty.</summary>
    public static bool IsWakeFromSleep(TimeSpan elapsed, int thresholdSecs) =>
        elapsed.TotalSeconds > thresholdSecs;

    /// <summary>Parse <c>Retry-After</c> as integer seconds, honoring only (0, 120] (the SSE cap).</summary>
    private static int? ParseRetryAfterSecs(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }
        var raw = values.FirstOrDefault();
        if (raw is null || !int.TryParse(raw, out var secs))
        {
            return null;
        }
        return secs is > 0 and <= 120 ? secs : null;
    }
}
