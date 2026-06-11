using System.Collections.ObjectModel;
using ChllSeeder.App.Services;
using ChllSeeder.Core.Api;
using ChllSeeder.Core.Bootstrap;
using ChllSeeder.Core.Config;
using ChllSeeder.Core.Games;
using ChllSeeder.Core.Seeding;
using ChllSeeder.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ChllSeeder.App.ViewModels;

/// <summary>
/// Shared (singleton) view model behind the Seed and Launch tabs. Drives the
/// <see cref="SeedingEngine"/>, mirrors its events into observable UI state, and owns the
/// server-list/stats banner fed by <see cref="AppBootstrapper"/>. Port of the action helpers
/// + signal state in src-rust/src/components/seed.rs and launch.rs.
/// </summary>
public sealed partial class SeedingViewModel : ObservableObject
{
    private readonly ILogger<SeedingViewModel> _log;
    private readonly SeedingEngine _engine;
    private readonly SeedingApiClient _api;
    private readonly ServerStore _servers;
    private readonly ConfigService _config;
    private readonly AppBootstrapper _bootstrap;
    private readonly LiveStats _live;
    private readonly AuthSession _auth;
    private readonly HeartbeatService _heartbeat;
    private readonly ToastService _toast;
    private readonly InAppToastService _inAppToast;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer;

    /// <summary>The active seeding session id (from start-session), heartbeat-ed until stop. Null
    /// when no session is open. Analytics only — non-fatal if session creation failed.</summary>
    private string? _sessionId;

    /// <summary>True while a "Seed All" rotation is running: after each server finishes seeding the VM
    /// fetches the next best candidate and re-launches. Cleared by a stop or when all are exhausted.
    /// Port of the Rust IS_SEED_ALL signal.</summary>
    private bool _isSeedAll;

    /// <summary>Set by the hosting page (which has a XamlRoot) so the VM can ask the user to
    /// confirm closing a running game. Returns true when the user confirms.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public SeedingViewModel(
        ILogger<SeedingViewModel> log,
        SeedingEngine engine,
        SeedingApiClient api,
        ServerStore servers,
        ConfigService config,
        AppBootstrapper bootstrap,
        LiveStats live,
        AuthSession auth,
        HeartbeatService heartbeat,
        ToastService toast,
        InAppToastService inAppToast)
    {
        _log = log;
        _engine = engine;
        _api = api;
        _servers = servers;
        _config = config;
        _bootstrap = bootstrap;
        _live = live;
        _auth = auth;
        _heartbeat = heartbeat;
        _toast = toast;
        _inAppToast = inAppToast;

        // Constructed on the UI thread (first page resolve), so this captures the UI queue.
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        euEnabled = _config.GetBool("eu_enabled");
        efficiencyMode = _config.GetBool("efficiency_mode");

        _engine.Event += OnEngineEvent;
        _bootstrap.ServersLoaded += OnServersLoaded;
        _bootstrap.ServersLoadFailed += OnServersLoadFailed;
        _live.StatsUpdated += OnStatsUpdated;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // If the bootstrapper already loaded servers before this VM existed, reflect that now.
        if (_bootstrap.HasServers)
        {
            RebuildServers();
        }

        // Compute the initial button/banner visibility (RefreshDerived otherwise only runs on a
        // Status/IsSeeding/ServersReady change, so the seed buttons would keep their default).
        RefreshDerived();
    }

    // ── Observable state ───────────────────────────────────────────────────────

    [ObservableProperty]
    private SeedingStatus status = SeedingStatus.Idle;

    /// <summary>True for the seeding flow (Seed tab), false for a bare launch (Launch tab).
    /// Mirrors the Rust IS_SEEDING signal that picks the one-vs-two stop-button layout.</summary>
    [ObservableProperty]
    private bool isSeeding;

    [ObservableProperty]
    private bool euEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopSeedingOnly))]
    private bool efficiencyMode;

    /// <summary>Whether "Stop Seeding (keep game)" is offered. Disabled in efficiency mode, where
    /// the game window is minimized and the user should fully stop instead (matches the Rust UI).</summary>
    public bool CanStopSeedingOnly => !EfficiencyMode;

    [ObservableProperty]
    private bool serversReady;

    [ObservableProperty]
    private bool serverLoadError;

    [ObservableProperty]
    private string statusBanner = "";

    [ObservableProperty]
    private bool showStatusBanner;

    // Error banners are page-scoped: a Seed-page failure (e.g. "All NA servers are full") must not
    // bleed onto the Launch page and vice-versa. Each page binds only its own pair. The lifecycle
    // status banner above (StatusBanner) stays shared because active seeding is genuine global state.
    [ObservableProperty]
    private string seedError = "";

    [ObservableProperty]
    private bool showSeedError;

    [ObservableProperty]
    private string launchError = "";

    [ObservableProperty]
    private bool showLaunchError;

    // Hidden until a successful server-list response (RefreshDerived gates on ServersReady).
    [ObservableProperty]
    private bool showSeedButtons;

    [ObservableProperty]
    private bool showActiveSeeding;

    [ObservableProperty]
    private bool showWaitingForUpdate;

    // Splash-bypass banner
    [ObservableProperty]
    private bool splashBypassActive;

    [ObservableProperty]
    private string splashBypassText = "";

    private long _splashRemaining;

    // Server-switch overlay
    [ObservableProperty]
    private bool serverSwitchActive;

    [ObservableProperty]
    private bool serverSwitchSnoozed;

    [ObservableProperty]
    private string serverSwitchTitle = "";

    [ObservableProperty]
    private string serverSwitchServerName = "";

    [ObservableProperty]
    private string serverSwitchCountdownText = "";

    private long _switchCountdown;
    private long _switchSnoozeRemaining;

    public ObservableCollection<ServerRow> NaServers { get; } = [];
    public ObservableCollection<ServerRow> EuServers { get; } = [];

    // ── Status transitions ─────────────────────────────────────────────────────

    private void SetStatus(SeedingStatus value) => Status = value;

    partial void OnStatusChanged(SeedingStatus value) => RefreshDerived();

    partial void OnIsSeedingChanged(bool value) => RefreshDerived();

    partial void OnServersReadyChanged(bool value) => RefreshDerived();

    /// <summary>Which of the two banner-bearing pages is currently shown. Stop/update failures can
    /// be triggered from either (both carry the active-seeding stop controls), so they surface on
    /// the page the user is actually looking at rather than always defaulting to one. Set by each
    /// page's OnNavigatedTo.</summary>
    private bool _launchPageActive;

    /// <summary>Records the visible page so shared (non-page-specific) errors land on it.</summary>
    public void SetActivePage(bool isLaunchPage) => _launchPageActive = isLaunchPage;

    /// <summary>Dismiss the Seed page's error banner. Called when navigating away from the page so a
    /// stale error doesn't linger on the next visit.</summary>
    public void ClearSeedError()
    {
        SeedError = "";
        ShowSeedError = false;
    }

    /// <summary>Dismiss the Launch page's error banner. Called when navigating away from the page so a
    /// stale error doesn't linger on the next visit.</summary>
    public void ClearLaunchError()
    {
        LaunchError = "";
        ShowLaunchError = false;
    }

    /// <summary>Show an error on whichever page is currently visible (for failures not tied to a
    /// specific page's action, e.g. a failed stop or an update timeout).</summary>
    private void SetActivePageError(string message)
    {
        if (_launchPageActive)
        {
            SetLaunchError(message);
        }
        else
        {
            SetSeedError(message);
        }
    }

    /// <summary>Show a page-scoped error on the Seed page (seeding/stop/update failures) and drop
    /// back to an idle state so the seed buttons reappear. Clears any stale Launch-page error.</summary>
    private void SetSeedError(string message)
    {
        LaunchError = "";
        ShowLaunchError = false;
        SeedError = message;
        ShowSeedError = true;
        IsSeeding = false;
        SetStatus(SeedingStatus.Idle);
    }

    /// <summary>Show a page-scoped error on the Launch page (launch failures / blocked launches) and
    /// drop back to an idle state so the launch buttons reappear. Clears any stale Seed-page error.</summary>
    private void SetLaunchError(string message)
    {
        SeedError = "";
        ShowSeedError = false;
        LaunchError = message;
        ShowLaunchError = true;
        IsSeeding = false;
        SetStatus(SeedingStatus.Idle);
    }

    /// <summary>Recompute the banner text and which button group is visible. Mirrors the
    /// show_* derivations in seed.rs and the status_config map in seed_banner.rs.</summary>
    private void RefreshDerived()
    {
        // Gate the seed buttons on a definite, successful server-list response: while the list is
        // still loading (indeterminate) or failed, the loading/retry block is shown instead, so a
        // user can't kick off a seed against an unconfirmed server list.
        ShowSeedButtons = ServersReady
            && (Status is SeedingStatus.Idle or SeedingStatus.Stopped
                || (Status == SeedingStatus.Running && !IsSeeding));

        ShowWaitingForUpdate = Status == SeedingStatus.WaitingForUpdate;

        ShowActiveSeeding = Status is SeedingStatus.Initializing or SeedingStatus.Seeding
            or SeedingStatus.Stopping or SeedingStatus.Switching
            || (Status == SeedingStatus.Running && IsSeeding);

        var banner = Status switch
        {
            SeedingStatus.Initializing => "Initializing",
            SeedingStatus.Running => "Game Running",
            SeedingStatus.Seeding => "Seeding",
            SeedingStatus.Stopping => "Stopping…",
            SeedingStatus.Switching => "Switching…",
            SeedingStatus.Stopped => "Stopped",
            SeedingStatus.WaitingForUpdate => "Waiting for game update…",
            _ => "",
        };
        StatusBanner = banner;
        ShowStatusBanner = banner.Length > 0;
    }

    private bool IsBusy => Status is SeedingStatus.Initializing or SeedingStatus.Seeding
        or SeedingStatus.Stopping or SeedingStatus.Switching or SeedingStatus.Running;

    // ── Seed commands (Seed tab) ───────────────────────────────────────────────

    [RelayCommand]
    private Task SeedNaAsync() => SeedRegionAsync("na");

    [RelayCommand]
    private Task SeedEuAsync() => SeedRegionAsync("eu");

    /// <summary>Fetch the best candidate for a region from the status endpoint, then launch and
    /// monitor it. Port of do_start_seeding + start_seeding_inner.</summary>
    private async Task SeedRegionAsync(string region)
    {
        if (IsBusy)
        {
            return;
        }
        if (ServerLoadError)
        {
            await _bootstrap.LoadServersAsync().ConfigureAwait(true);
            return;
        }

        ClearSeedError();
        IsSeeding = true;
        SetStatus(SeedingStatus.Initializing);

        SeedingStatusResponse status;
        try
        {
            status = await _api.GetSeedingStatusAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to fetch seeding status");
            SetSeedError("Couldn't reach the seeding service. Please try again.");
            return;
        }

        var candidate = region == "eu" ? status.Hll.Eu : status.Hll.Na;
        if (candidate is null)
        {
            SetSeedError(region == "eu"
                ? "All EU servers are full — no seeding needed."
                : "All NA servers are full — no seeding needed.");
            return;
        }

        if (!await ConfirmCloseRunningGameAsync().ConfigureAwait(true))
        {
            IsSeeding = false;
            SetStatus(SeedingStatus.Idle);
            return;
        }

        await RunSeedAsync(candidate.Index, region).ConfigureAwait(true);
    }

    /// <summary>Launch + monitor a specific candidate index, then resolve the terminal status.
    /// Single-server path; Seed All uses <see cref="LaunchAndMonitorAsync"/> directly so it can rotate.</summary>
    private async Task RunSeedAsync(int index, string region)
    {
        if (!await LaunchAndMonitorAsync(index, region).ConfigureAwait(true))
        {
            // Handed off to the launch watcher (WaitingForUpdate) or failed — state already set.
            return;
        }

        // Monitor returned: HLL closed, switched away, or a stop was requested.
        if (Status != SeedingStatus.WaitingForUpdate)
        {
            SetStatus(Status == SeedingStatus.Stopping ? SeedingStatus.Stopped : SeedingStatus.Idle);
        }
        IsSeeding = false;
    }

    /// <summary>Launch a candidate, open its analytics session, and run the monitor loop to completion.
    /// Returns <c>true</c> when the monitor returned normally (the caller may continue/rotate), or
    /// <c>false</c> when the flow handed off to the launch watcher (WaitingForUpdate) or failed — in
    /// which case the status/error has already been set. Port of start_seeding_inner + the monitor await.</summary>
    private async Task<bool> LaunchAndMonitorAsync(int index, string region)
    {
        SetStatus(SeedingStatus.Initializing);
        try
        {
            var actual = await _engine.StartSeedingAsync(index, region).ConfigureAwait(true);
            SetStatus(SeedingStatus.Seeding);

            // Create the analytics session + heartbeat after a successful launch (non-fatal).
            await StartSessionAsync(region, actual).ConfigureAwait(true);

            await _engine.MonitorSeedAsync(actual, region).ConfigureAwait(true);

            // Monitor returned on its own (HLL closed / switched / stop) — end the session.
            await StopSessionAsync("monitor_complete").ConfigureAwait(true);
            return true;
        }
        catch (SeedingException e) when (e.Message.Contains("could not open", StringComparison.OrdinalIgnoreCase))
        {
            // The engine restored settings and spawned the launch watcher; its events
            // (SeedingUpdate*) drive the rest of the flow from here.
            SetStatus(SeedingStatus.WaitingForUpdate);
            return false;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Seeding failed");
            SetSeedError("Failed to launch the game. Please try again.");
            return false;
        }
    }

    // ── Seed All rotation (Seed tab) ────────────────────────────────────────────

    /// <summary>One step of a Seed All rotation: which game/region/server to seed next.</summary>
    private sealed record SeedAllHop(string GameId, string Region, int Index);

    /// <summary>Rotate across every region (and enabled game) until all are seeded or stopped. After
    /// each server finishes seeding, fetches the freshest status and hops to the next best candidate.
    /// Port of do_seed_next_server + the IS_SEED_ALL branch of do_monitor_seed.</summary>
    [RelayCommand]
    private async Task SeedAllAsync()
    {
        if (IsBusy)
        {
            return;
        }
        if (ServerLoadError)
        {
            await _bootstrap.LoadServersAsync().ConfigureAwait(true);
            return;
        }

        ClearSeedError();
        IsSeeding = true;
        _isSeedAll = true;
        SetStatus(SeedingStatus.Initializing);

        var euEnabled = _config.GetBool("eu_enabled");

        // First candidate across enabled games (NA preferred, then EU when enabled).
        SeedAllHop? hop;
        try
        {
            var status = await _api.GetSeedingStatusAsync().ConfigureAwait(true);
            hop = PickInitialHop(status, euEnabled);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Seed All: failed to fetch seeding status");
            _isSeedAll = false;
            SetSeedError("Couldn't reach the seeding service. Please try again.");
            return;
        }

        if (hop is null)
        {
            _isSeedAll = false;
            SetSeedError("All servers are full or offline — no seeding needed.");
            return;
        }

        if (!await ConfirmCloseRunningGameAsync().ConfigureAwait(true))
        {
            _isSeedAll = false;
            IsSeeding = false;
            SetStatus(SeedingStatus.Idle);
            return;
        }

        while (_isSeedAll && hop is not null)
        {
            // Cross-game hop: kill the current game, wait, then switch (only fires when more than
            // one game is enabled — HLLV is scaffolded but not yet released).
            if (hop.GameId != _engine.CurrentGame.Id)
            {
                _log.LogInformation("Seed All: switching game {From} -> {To}", _engine.CurrentGame.Id, hop.GameId);
                try
                {
                    await _engine.KillGameAndWaitAsync().ConfigureAwait(true);
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "Seed All: kill before game switch failed");
                }
                if (GameCatalog.ById(hop.GameId) is { } game)
                {
                    _engine.CurrentGame = game;
                }
            }

            if (!await LaunchAndMonitorAsync(hop.Index, hop.Region).ConfigureAwait(true))
            {
                // Launch watcher took over (WaitingForUpdate) or a launch failed (error shown).
                _isSeedAll = false;
                return; // status / IsSeeding already in the right state
            }
            if (!_isSeedAll)
            {
                break; // user stopped during this hop
            }

            try
            {
                var status = await _api.GetSeedingStatusAsync().ConfigureAwait(true);
                hop = PickNextHop(status, _engine.CurrentGame.Id, hop.Region, euEnabled);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Seed All: failed to fetch next status");
                hop = null; // stop rotating; treat as exhausted
            }
        }

        var stoppedByUser = !_isSeedAll && Status == SeedingStatus.Stopping;
        _isSeedAll = false;

        if (!stoppedByUser && hop is null && Status != SeedingStatus.WaitingForUpdate)
        {
            _inAppToast.Success("Seed All complete — every server is seeded.");
        }

        if (Status != SeedingStatus.WaitingForUpdate)
        {
            SetStatus(Status == SeedingStatus.Stopping ? SeedingStatus.Stopped : SeedingStatus.Idle);
        }
        IsSeeding = false;
    }

    /// <summary>First Seed All candidate: walk enabled games, taking NA (or EU when enabled).</summary>
    private SeedAllHop? PickInitialHop(SeedingStatusResponse status, bool euEnabled)
    {
        foreach (var gameId in GetEnabledGames())
        {
            if (GameStatus(status, gameId) is { } gs && PreferNaThenEu(gs, gameId, euEnabled) is { } hop)
            {
                return hop;
            }
        }
        return null;
    }

    /// <summary>Next Seed All candidate: stay on the current game/region first (region-preferred), then
    /// fall through to the other enabled games. Port of the do_monitor_seed rotation cascade.</summary>
    private SeedAllHop? PickNextHop(SeedingStatusResponse status, string currentGameId, string currentRegion, bool euEnabled)
    {
        if (GameStatus(status, currentGameId) is { } cur)
        {
            var hop = currentRegion == "eu"
                ? PreferEuThenNa(cur, currentGameId, euEnabled)
                : PreferNaThenEu(cur, currentGameId, euEnabled);
            if (hop is not null)
            {
                return hop;
            }
        }

        foreach (var gameId in GetEnabledGames())
        {
            if (gameId == currentGameId)
            {
                continue;
            }
            if (GameStatus(status, gameId) is { } gs && PreferNaThenEu(gs, gameId, euEnabled) is { } hop)
            {
                return hop;
            }
        }
        return null;
    }

    private static SeedAllHop? PreferNaThenEu(GameSeedingStatus gs, string gameId, bool euEnabled)
    {
        if (gs.Na is { } na)
        {
            return new SeedAllHop(gameId, "na", na.Index);
        }
        if (euEnabled && gs.Eu is { } eu)
        {
            return new SeedAllHop(gameId, "eu", eu.Index);
        }
        return null;
    }

    private static SeedAllHop? PreferEuThenNa(GameSeedingStatus gs, string gameId, bool euEnabled)
    {
        if (gs.Eu is { } eu)
        {
            return new SeedAllHop(gameId, "eu", eu.Index);
        }
        if (euEnabled && gs.Na is { } na)
        {
            return new SeedAllHop(gameId, "na", na.Index);
        }
        return null;
    }

    private static GameSeedingStatus? GameStatus(SeedingStatusResponse status, string gameId) => gameId switch
    {
        "hll" => status.Hll,
        "hllv" => status.Hllv,
        _ => null,
    };

    /// <summary>Enabled game IDs from config, filtered to released games (defaults to HLL only).
    /// Port of get_enabled_games.</summary>
    private IReadOnlyList<string> GetEnabledGames()
    {
        var released = GameCatalog.Released.Select(g => g.Id).ToHashSet();
        var configured = _config.Get("enabled_games", new List<string> { GameCatalog.Hll.Id })
            ?? new List<string> { GameCatalog.Hll.Id };
        var filtered = configured.Where(released.Contains).ToList();
        return filtered.Count > 0 ? filtered : new List<string> { GameCatalog.Hll.Id };
    }

    // ── Seeding session + heartbeat (analytics; non-fatal) ─────────────────────

    /// <summary>Create a seeding session after a successful launch and start its heartbeat.
    /// Guest/auth required; failures are logged and swallowed (mirrors the Rust seed flow).</summary>
    private async Task StartSessionAsync(string region, int index)
    {
        if (!_auth.IsAuthenticated)
        {
            return;
        }
        try
        {
            var analytics = Analytics.Gather(_config, autoSeed: false);
            var resp = await _api.StartSessionAsync(
                _engine.CurrentGame.Id, region, index, steamId: null, analytics).ConfigureAwait(true);
            _sessionId = resp.SessionId;
            await _heartbeat.StartAsync(resp.SessionId).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to create seeding session (non-fatal)");
        }
    }

    /// <summary>Stop the heartbeat and end the session with a reason. No-op when no session is open.</summary>
    private async Task StopSessionAsync(string reason)
    {
        if (_sessionId is null)
        {
            return;
        }
        _sessionId = null;
        await _heartbeat.StopAsync(reason).ConfigureAwait(true);
    }

    // ── Launch command (Launch tab) ────────────────────────────────────────────

    /// <summary>Directly launch a specific server (no seeding monitor). Port of start_server.</summary>
    [RelayCommand]
    private async Task LaunchAsync(ServerRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        // Same exclusions the seeding path gets from backend candidate filtering: never launch
        // into an offline server (nothing to join) or a passworded one (HLL can't join via the
        // launcher). The button is disabled for these too; this guards the gap before stats land.
        if (!row.CanLaunch)
        {
            SetLaunchError(row.IsOffline
                ? $"{row.ShortName} is offline right now."
                : $"{row.ShortName} is password-protected — HLL can't join it via the launcher.");
            return;
        }

        ClearLaunchError();
        IsSeeding = false;
        SetStatus(SeedingStatus.Initializing);

        if (!await ConfirmCloseRunningGameAsync().ConfigureAwait(true))
        {
            SetStatus(SeedingStatus.Idle);
            return;
        }

        try
        {
            await _engine.StartAsync(row.Index, row.Region).ConfigureAwait(true);
            SetStatus(SeedingStatus.Running);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Launch failed");
            SetLaunchError("Failed to launch the game. Please try again.");
        }
    }

    /// <summary>If the game is already running, ask the user to confirm closing it and wait for
    /// exit. Returns false only when the user declines. Mirrors the show_confirm path.</summary>
    private async Task<bool> ConfirmCloseRunningGameAsync()
    {
        if (!_engine.IsGameRunning)
        {
            return true;
        }

        SetStatus(SeedingStatus.Idle);
        var confirmed = ConfirmAsync is null
            || await ConfirmAsync(
                $"{_engine.CurrentGame.DisplayName} is currently running. Close the game to continue?",
                "Game Running").ConfigureAwait(true);
        if (!confirmed)
        {
            return false;
        }

        SetStatus(SeedingStatus.Initializing);
        await _engine.KillGameAndWaitAsync().ConfigureAwait(true);
        return true;
    }

    // ── Stop commands ──────────────────────────────────────────────────────────

    /// <summary>Stop seeding and close the game. Port of do_stop_seed / stop_game.</summary>
    [RelayCommand]
    private async Task StopGameAsync()
    {
        _isSeedAll = false;
        SetStatus(SeedingStatus.Stopping);
        ResetSwitchOverlay();
        await StopSessionAsync("user_stopped").ConfigureAwait(true);
        try
        {
            await _engine.StopSeedingAsync().ConfigureAwait(true);
            SetStatus(SeedingStatus.Stopped);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error stopping seeding");
            SetActivePageError("Failed to stop seeding cleanly.");
        }
        IsSeeding = false;
    }

    /// <summary>Stop the seeding monitor but leave the game running. Port of do_stop_seed_only.</summary>
    [RelayCommand]
    private async Task StopSeedingOnlyAsync()
    {
        _isSeedAll = false;
        SetStatus(SeedingStatus.Stopping);
        ResetSwitchOverlay();
        await StopSessionAsync("user_stopped_keep_game").ConfigureAwait(true);
        try
        {
            await _engine.StopSeedingOnlyAsync().ConfigureAwait(true);
            SetStatus(SeedingStatus.Stopped);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error stopping seeding (keep game)");
            SetActivePageError("Failed to stop seeding cleanly.");
        }
        IsSeeding = false;
    }

    [RelayCommand]
    private Task RetryServersAsync() => _bootstrap.LoadServersAsync();

    // ── Server-switch overlay commands ─────────────────────────────────────────

    [RelayCommand]
    private void Snooze(string seconds)
    {
        if (long.TryParse(seconds, out var secs))
        {
            _engine.SnoozeServerSwitch(secs);
        }
    }

    [RelayCommand]
    private void SwitchNow()
    {
        _engine.ConfirmServerSwitch();
        ResetSwitchOverlay();
    }

    // ── Engine events (raised off-thread → marshal to UI) ──────────────────────

    private void OnEngineEvent(SeedingEvent e) => _dispatcher.TryEnqueue(() => HandleEngineEvent(e));

    private void HandleEngineEvent(SeedingEvent e)
    {
        switch (e)
        {
            case SeedingEvent.SplashBypassStarted s:
                _splashRemaining = s.DurationSecs;
                SplashBypassActive = true;
                UpdateSplashText();
                break;
            case SeedingEvent.SplashBypassComplete:
            case SeedingEvent.SplashBypassTimeout:
                SplashBypassActive = false;
                break;

            case SeedingEvent.HllClosed:
                ResetSwitchOverlay();
                SplashBypassActive = false;
                SetStatus(SeedingStatus.Stopped);
                IsSeeding = false;
                break;

            case SeedingEvent.ServerSwitchPending p:
                ShowSwitchOverlay(p);
                // Desktop toast + attention sound (the engine only emits this when
                // switch_notification is enabled, so no extra gating needed here).
                _toast.ShowServerSwitch(p.ServerName, p.CountdownSecs);
                break;
            case SeedingEvent.ServerSwitchSnoozed sn:
                ServerSwitchSnoozed = true;
                _switchSnoozeRemaining = sn.SnoozeSecs;
                UpdateSwitchText();
                break;
            case SeedingEvent.ServerSwitchExecuting:
                ResetSwitchOverlay();
                SetStatus(SeedingStatus.Switching);
                _inAppToast.Info("Switching to the next server…");
                break;
            case SeedingEvent.ServerSwitchCancelled:
                ResetSwitchOverlay();
                break;

            case SeedingEvent.SeedingUpdateWaiting:
                SetStatus(SeedingStatus.WaitingForUpdate);
                break;
            case SeedingEvent.SeedingUpdateStarted:
                IsSeeding = true;
                SetStatus(SeedingStatus.Seeding);
                break;
            case SeedingEvent.SeedingUpdateTimeout:
                SetActivePageError("The game didn't finish updating in time. Please try again.");
                break;
        }
    }

    private void ShowSwitchOverlay(SeedingEvent.ServerSwitchPending p)
    {
        ServerSwitchServerName = p.ServerName;
        ServerSwitchTitle = p.Reason switch
        {
            "candidate_changed" => "Server Switching",
            "time_limit" => "Time Limit Reached",
            _ => "Server Switching",
        };
        _switchCountdown = p.CountdownSecs;
        ServerSwitchSnoozed = false;
        ServerSwitchActive = true;
        UpdateSwitchText();
    }

    private void ResetSwitchOverlay()
    {
        ServerSwitchActive = false;
        ServerSwitchSnoozed = false;
        _switchCountdown = 0;
        _switchSnoozeRemaining = 0;
    }

    // ── Bootstrapper events ────────────────────────────────────────────────────

    private void OnServersLoaded() => _dispatcher.TryEnqueue(RebuildServers);

    private void OnServersLoadFailed() => _dispatcher.TryEnqueue(() =>
    {
        ServerLoadError = true;
        ServersReady = false;
    });

    private void OnStatsUpdated(IReadOnlyList<BatchStatsResult> stats) =>
        _dispatcher.TryEnqueue(() => ApplyStats(stats));

    private void RebuildServers()
    {
        ServerLoadError = false;
        EuEnabled = _config.GetBool("eu_enabled");

        BuildList(NaServers, _servers.GetServers(), "na");
        BuildList(EuServers, _servers.GetEuServers(), "eu");

        ServersReady = NaServers.Count > 0;
    }

    private static void BuildList(ObservableCollection<ServerRow> target, IReadOnlyList<ServerInfo> servers, string region)
    {
        target.Clear();
        for (var i = 0; i < servers.Count; i++)
        {
            target.Add(new ServerRow(i, region, servers[i]));
        }
    }

    private void ApplyStats(IReadOnlyList<BatchStatsResult> stats)
    {
        ApplyTo(NaServers, "na", stats);
        ApplyTo(EuServers, "eu", stats);
    }

    private static void ApplyTo(ObservableCollection<ServerRow> rows, string region, IReadOnlyList<BatchStatsResult> stats)
    {
        foreach (var row in rows)
        {
            BatchStatsResult? match = null;
            foreach (var s in stats)
            {
                if (s.Game == "hll" && s.Region == region && s.Index == row.Index)
                {
                    match = s;
                    break;
                }
            }
            row.Apply(match);
        }
    }

    // ── Countdown timer (visual only; the engine drives the real timing) ───────

    private void OnTimerTick(object? sender, object e)
    {
        if (SplashBypassActive && _splashRemaining > 0)
        {
            _splashRemaining--;
            UpdateSplashText();
        }

        if (ServerSwitchActive)
        {
            if (ServerSwitchSnoozed)
            {
                if (_switchSnoozeRemaining > 0)
                {
                    _switchSnoozeRemaining--;
                }
            }
            else if (_switchCountdown > 0)
            {
                _switchCountdown--;
            }
            UpdateSwitchText();
        }
    }

    private void UpdateSplashText() =>
        SplashBypassText = $"Skipping intro ({_splashRemaining / 60}:{_splashRemaining % 60:D2} remaining)";

    private void UpdateSwitchText()
    {
        if (ServerSwitchSnoozed)
        {
            ServerSwitchCountdownText = $"{_switchSnoozeRemaining / 60}m {_switchSnoozeRemaining % 60}s";
        }
        else
        {
            ServerSwitchCountdownText = $"{_switchCountdown}s";
        }
    }
}
