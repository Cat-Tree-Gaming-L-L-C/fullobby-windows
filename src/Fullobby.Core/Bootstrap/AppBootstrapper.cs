using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Fullobby.Core.Scheduling;
using Fullobby.Core.Servers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Bootstrap;

/// <summary>
/// App-startup background worker: loads the server list (guest-OK, no identity needed),
/// then runs the HTTP stats-poll <em>fallback</em> for the SSE stream. Guest identities are
/// <em>not</em> minted here — that only happens through the interactive, Turnstile-verified
/// onboarding flow (<c>AccountViewModel.RegisterGuestAsync</c>), so a background start can't
/// bypass verification. While the SSE stream
/// is connected the poll idles (a slow safety net); when SSE drops it polls aggressively and
/// is woken immediately on disconnect via <see cref="SseConnectionState"/>. Applied stats and
/// the server-list events drive the UI. Registered as an <see cref="IHostedService"/>.
/// </summary>
public sealed class AppBootstrapper : IHostedService
{
    private readonly ILogger<AppBootstrapper> _log;
    private readonly SeedingApiClient _api;
    private readonly ServerStore _servers;
    private readonly ConfigService _config;
    private readonly LiveStats _live;
    private readonly SseConnectionState _sse;
    private readonly SeedingConfigProvider _configProvider;
    private readonly SeedingStatusCache _statusCache;
    private readonly AutoSeedService _autoSeed;

    private readonly CancellationTokenSource _cts = new();
    private Task? _runLoop;

    public AppBootstrapper(
        ILogger<AppBootstrapper> log,
        SeedingApiClient api,
        ServerStore servers,
        ConfigService config,
        LiveStats live,
        SseConnectionState sse,
        SeedingConfigProvider configProvider,
        SeedingStatusCache statusCache,
        AutoSeedService autoSeed)
    {
        _log = log;
        _api = api;
        _servers = servers;
        _config = config;
        _live = live;
        _sse = sse;
        _configProvider = configProvider;
        _statusCache = statusCache;
        _autoSeed = autoSeed;
    }

    /// <summary>Raised after the server list is (re)loaded successfully.</summary>
    public event Action? ServersLoaded;

    /// <summary>Raised when the server list could not be loaded.</summary>
    public event Action? ServersLoadFailed;

    /// <summary>True once the server list has been loaded at least once.</summary>
    public bool HasServers { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Every fresh seeding status may carry moved per-network windows — reconcile the wake set
        // against it. Cheap when nothing changed (in-memory compare, no process spawns), so the
        // push cadence is fine; AutoSeedService serializes overlapping calls itself.
        _statusCache.Updated += OnStatusUpdated;

        // Run init + polling off the host-start thread so the first window paint isn't
        // blocked on network I/O.
        _runLoop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private void OnStatusUpdated(SeedingStatusResponse status) =>
        _ = ReconcileWakesAsync(_cts.Token);

    private async Task ReconcileWakesAsync(CancellationToken ct)
    {
        try
        {
            await _autoSeed.ReconcileAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception e)
        {
            _log.LogWarning(e, "Auto-seed wake reconcile failed");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _statusCache.Updated -= OnStatusUpdated;
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (Exception e) { _log.LogDebug(e, "Bootstrapper cancel on shutdown failed"); }

        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); }
            catch (Exception e) { _log.LogDebug(e, "Bootstrapper run loop ended with error"); }
        }

        _config.FlushPendingSaves();
    }

    /// <summary>How often the server-decided config is re-fetched inside the poll loop.</summary>
    private const long ConfigRefreshIntervalMs = 15 * 60 * 1000;

    private long _lastConfigRefreshTicks;

    private async Task RunAsync(CancellationToken ct)
    {
        _lastConfigRefreshTicks = Environment.TickCount64;
        await RefreshConfigAsync(ct).ConfigureAwait(false);

        // Startup reconcile: adopts a pre-wake-set install's single "Fullobby" task into the
        // time-keyed set, and re-arms against a fleet window that moved while the app was closed.
        await ReconcileWakesAsync(ct).ConfigureAwait(false);

        await LoadServersAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            // Idle slowly while SSE feeds data; poll fast (and wake on disconnect) when it doesn't.
            // Intervals come from the server config (with the baked-in defaults as fallback).
            var cfg = _configProvider.Current;
            var interval = _sse.Connected
                ? TimeSpan.FromSeconds(Math.Max(1, cfg.PollIdleSecs))
                : TimeSpan.FromSeconds(Math.Max(1, cfg.PollFallbackSecs));
            try { await _sse.WaitForPollOrInterval(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Refresh the server-decided config so timing changes propagate without a restart —
            // but on its own slow cadence, not once per poll loop. Tied to the loop it ran every
            // PollIdleSecs (60s => ~1,440 requests/client/day) in the healthy steady state, for a
            // value that changes rarely and that every seeding directive already carries inline.
            if (Environment.TickCount64 - _lastConfigRefreshTicks >= ConfigRefreshIntervalMs)
            {
                _lastConfigRefreshTicks = Environment.TickCount64;
                await RefreshConfigAsync(ct).ConfigureAwait(false);
                // The refreshed config may have moved the fleet windows the fallback wake follows.
                await ReconcileWakesAsync(ct).ConfigureAwait(false);
            }

            // Skip the redundant HTTP round-trip if SSE reconnected while we waited.
            if (_sse.Connected)
            {
                continue;
            }
            await PollStatsAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fetch the seeding config and publish it. Non-fatal — keeps the previous (or baked-in)
    /// config on failure so the app still works when the endpoint is unreachable.</summary>
    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        try
        {
            var cfg = await _api.GetSeedingConfigAsync(ct).ConfigureAwait(false);
            _configProvider.Update(cfg);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Seeding config fetch failed (keeping current config)");
        }
    }

    /// <summary>Fetch the server list and load it into the store. Public so the UI's retry
    /// button can re-run it after a load failure.</summary>
    public async Task LoadServersAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _api.GetServersAsync(ct).ConfigureAwait(false);
            _servers.Load(resp);
            HasServers = true;
            _log.LogInformation("Loaded {Count} servers", resp.Hll.Count);
            ServersLoaded?.Invoke();

            // Seed the banner immediately rather than waiting a full poll interval.
            await PollStatsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to load server list");
            ServersLoadFailed?.Invoke();
        }
    }

    private async Task PollStatsAsync(CancellationToken ct)
    {
        if (!HasServers)
        {
            return;
        }
        try
        {
            var stats = await _api.GetStatsAsync(ct).ConfigureAwait(false);
            _live.Apply(stats);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Stats poll failed (will retry next interval)");
        }
    }
}
