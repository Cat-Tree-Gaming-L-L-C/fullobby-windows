using ChllSeeding.Core.Api;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Servers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Bootstrap;

/// <summary>
/// App-startup background worker: ensures a guest identity exists, loads the server list,
/// then runs the HTTP stats-poll <em>fallback</em> for the SSE stream. While the SSE stream
/// is connected the poll idles (a slow safety net); when SSE drops it polls aggressively and
/// is woken immediately on disconnect via <see cref="SseConnectionState"/>. Applied stats and
/// the server-list events drive the UI. Registered as an <see cref="IHostedService"/>.
/// </summary>
public sealed class AppBootstrapper : IHostedService
{
    /// <summary>Poll cadence while SSE is disconnected (the active fallback).</summary>
    public static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Poll cadence while SSE is connected (a slow safety net for missed events).</summary>
    public static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<AppBootstrapper> _log;
    private readonly SeedingApiClient _api;
    private readonly AuthSession _auth;
    private readonly ServerStore _servers;
    private readonly ConfigService _config;
    private readonly LiveStats _live;
    private readonly SseConnectionState _sse;

    private readonly CancellationTokenSource _cts = new();
    private Task? _runLoop;

    public AppBootstrapper(
        ILogger<AppBootstrapper> log,
        SeedingApiClient api,
        AuthSession auth,
        ServerStore servers,
        ConfigService config,
        LiveStats live,
        SseConnectionState sse)
    {
        _log = log;
        _api = api;
        _auth = auth;
        _servers = servers;
        _config = config;
        _live = live;
        _sse = sse;
    }

    /// <summary>Raised after the server list is (re)loaded successfully.</summary>
    public event Action? ServersLoaded;

    /// <summary>Raised when the server list could not be loaded.</summary>
    public event Action? ServersLoadFailed;

    /// <summary>True once the server list has been loaded at least once.</summary>
    public bool HasServers { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run init + polling off the host-start thread so the first window paint isn't
        // blocked on network I/O.
        _runLoop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (Exception e) { _log.LogDebug(e, "Bootstrapper cancel on shutdown failed"); }

        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); }
            catch (Exception e) { _log.LogDebug(e, "Bootstrapper run loop ended with error"); }
        }

        _config.FlushPendingSaves();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await EnsureGuestAuthAsync(ct).ConfigureAwait(false);
        await LoadServersAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            // Idle slowly while SSE feeds data; poll fast (and wake on disconnect) when it doesn't.
            var interval = _sse.Connected ? IdlePollInterval : FallbackPollInterval;
            try { await _sse.WaitForPollOrInterval(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Skip the redundant HTTP round-trip if SSE reconnected while we waited.
            if (_sse.Connected)
            {
                continue;
            }
            await PollStatsAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Register a guest account when we have no credentials yet, so the authed
    /// endpoints (seeding status / next-server) work out of the box. Non-fatal on failure.</summary>
    private async Task EnsureGuestAuthAsync(CancellationToken ct)
    {
        if (_auth.IsAuthenticated)
        {
            return;
        }
        try
        {
            var resp = await _api.RegisterGuestAsync(ct).ConfigureAwait(false);
            _auth.SetApiKey(resp.ApiKey);
            _log.LogInformation("Registered guest identity {User}", resp.Username);
            // The SSE stream may have connected unauthenticated; reconnect so it sends the new key.
            _sse.RequestReconnect();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Guest registration failed (will retry on next start)");
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
            _log.LogInformation("Loaded {Na} NA / {Eu} EU servers", resp.Hll.Na.Count, resp.Hll.Eu.Count);
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
