using System.Diagnostics;
using ChllSeeding.Core.Api;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Seeding;

/// <summary>
/// Drives a seeding session's heartbeat: after a session is created it beats every
/// 30s (with exponential backoff on consecutive failures, capped at 5 min, and a
/// wake-from-sleep settle), and on stop it notifies the API the session ended.
/// At most one loop runs at a time — starting a new session stops the previous one.
/// Port of <c>src-rust/src/backend/heartbeat.rs</c> (the AtomicBool/Notify globals
/// become per-instance state). DI singleton; the loop runs off the UI thread.
/// </summary>
public sealed class HeartbeatService(SeedingApiClient api, SeedingConfigProvider configProvider, ILogger<HeartbeatService> log)
{
    public const long HeartbeatIntervalSecs = 30;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _sessionId;

    /// <summary>Start (or restart) the heartbeat loop for a session. Any existing loop is stopped
    /// first (without notifying the API — only an explicit <see cref="StopAsync"/> ends a session).</summary>
    public async Task StartAsync(string sessionId)
    {
        await StopLoopAsync().ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _sessionId = sessionId;
            _loop = Task.Run(() => RunAsync(sessionId, cts.Token));
        }
        log.LogInformation("Heartbeat started for session {SessionId}", sessionId);
    }

    /// <summary>Stop the heartbeat loop and notify the API that the session ended. Non-fatal.</summary>
    public async Task StopAsync(string? reason)
    {
        await StopLoopAsync().ConfigureAwait(false);

        string? sid;
        lock (_gate)
        {
            sid = _sessionId;
            _sessionId = null;
        }
        if (sid is null)
        {
            return;
        }

        try
        {
            await api.StopSessionAsync(sid, reason).ConfigureAwait(false);
            log.LogInformation("Seeding session {SessionId} stopped (reason: {Reason})", sid, reason);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Failed to stop session {SessionId}", sid);
        }
    }

    /// <summary>Fire-and-forget stop for non-async call sites (app exit). Spawns the stop so it
    /// doesn't block shutdown. Port of <c>stop_heartbeat_sync</c>.</summary>
    public void StopFireAndForget(string? reason) => _ = Task.Run(() => StopAsync(reason));

    private async Task StopLoopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is not null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); }
            catch (Exception e) { log.LogDebug(e, "Heartbeat cancel failed"); }
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception e) { log.LogDebug(e, "Heartbeat loop ended with error"); }
        }
        cts?.Dispose();
    }

    private async Task RunAsync(string sessionId, CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var backoff = BackoffIntervalSecs(consecutiveFailures, configProvider.Current.HeartbeatSecs);

            var before = Stopwatch.StartNew();
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var elapsed = before.Elapsed;

            // If the sleep ran far over (>3x), the system likely slept — settle the network first.
            if (elapsed.TotalSeconds > backoff * 3)
            {
                log.LogInformation(
                    "System wake detected in heartbeat loop (slept {Slept:F0}s vs {Expected}s), waiting 3s for network",
                    elapsed.TotalSeconds, backoff);
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            try
            {
                var resp = await api.SendHeartbeatAsync(sessionId, ct).ConfigureAwait(false);
                if (consecutiveFailures > 0)
                {
                    log.LogInformation("Heartbeat recovered after {Failures} failures", consecutiveFailures);
                }
                consecutiveFailures = 0;
                log.LogInformation("Heartbeat sent: count={Count}, duration={Secs}s, validated={Validated}",
                    resp.HeartbeatCount, resp.SessionDurationSecs, resp.Validated);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 10)
                {
                    log.LogError(e, "Heartbeat failed ({Failures} consecutive)", consecutiveFailures);
                }
                else
                {
                    log.LogWarning(e, "Heartbeat failed ({Failures} consecutive)", consecutiveFailures);
                }
            }
        }

        log.LogInformation("Heartbeat loop ended");
    }

    // ── Pure helper (unit-tested) ──────────────────────────────────────────────

    /// <summary>Heartbeat send interval: the base interval normally, else
    /// <c>min(interval * 2^min(failures,4), 300)</c> seconds of exponential backoff. The no-interval
    /// overload uses the baked-in default (30s) so the unit tests stay stable; the loop passes the
    /// server-configured <c>HeartbeatSecs</c>. Port of the backoff math in <c>heartbeat.rs</c>.</summary>
    public static long BackoffIntervalSecs(int consecutiveFailures) =>
        BackoffIntervalSecs(consecutiveFailures, HeartbeatIntervalSecs);

    /// <summary>Backoff over an explicit base interval (server-configured).</summary>
    public static long BackoffIntervalSecs(int consecutiveFailures, long intervalSecs)
    {
        var baseInterval = intervalSecs > 0 ? intervalSecs : HeartbeatIntervalSecs;
        if (consecutiveFailures <= 0)
        {
            return baseInterval;
        }
        var shift = Math.Min(consecutiveFailures, 4);
        return Math.Min(baseInterval * (1L << shift), 300);
    }
}
