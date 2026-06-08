using ChllSeeder.Core.Api;
using ChllSeeder.Core.Config;
using ChllSeeder.Core.Servers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChllSeeder.Core.Bootstrap;

/// <summary>
/// App-startup background worker: ensures a guest identity exists, loads the server list,
/// then polls live server stats on an interval (the Phase 1 stand-in for the Phase 2 SSE
/// stream). Raises events the UI subscribes to for the server list and the stats banner.
/// Registered as an <see cref="IHostedService"/> so it starts and stops with the host.
/// </summary>
public sealed class AppBootstrapper : IHostedService
{
    /// <summary>Stats polling interval. Phase 2 replaces polling with the SSE stream.</summary>
    public static readonly TimeSpan StatsPollInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<AppBootstrapper> _log;
    private readonly SeedingApiClient _api;
    private readonly AuthSession _auth;
    private readonly ServerStore _servers;
    private readonly ConfigService _config;

    private readonly CancellationTokenSource _cts = new();
    private Task? _runLoop;

    public AppBootstrapper(
        ILogger<AppBootstrapper> log,
        SeedingApiClient api,
        AuthSession auth,
        ServerStore servers,
        ConfigService config)
    {
        _log = log;
        _api = api;
        _auth = auth;
        _servers = servers;
        _config = config;
    }

    /// <summary>Raised after the server list is (re)loaded successfully.</summary>
    public event Action? ServersLoaded;

    /// <summary>Raised when the server list could not be loaded.</summary>
    public event Action? ServersLoadFailed;

    /// <summary>Raised after each successful stats poll, carrying the freshest batch.</summary>
    public event Action<IReadOnlyList<BatchStatsResult>>? StatsUpdated;

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
            try { await Task.Delay(StatsPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
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
            ApplyStats(stats);
            StatsUpdated?.Invoke(stats);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Stats poll failed (will retry next interval)");
        }
    }

    /// <summary>Push live counts/offline flags into the store so the seeding engine's
    /// fill-based stagger sees fresh data.</summary>
    private void ApplyStats(List<BatchStatsResult> stats)
    {
        foreach (var s in stats)
        {
            var server = _servers.GetGameServer(s.Game, s.Region, s.Index);
            if (server is null)
            {
                continue;
            }
            if (s.Offline)
            {
                _servers.MarkOffline(server.Name);
            }
            else
            {
                _servers.ClearOffline(server.Name);
            }
            if (s.PlayerCount is { } players)
            {
                _servers.UpdatePlayerCount(server.Name, players, s.MaxPlayerCount ?? 100);
            }
        }
    }
}
