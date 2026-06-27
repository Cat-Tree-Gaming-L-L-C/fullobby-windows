using System.Collections.ObjectModel;
using ChllSeeding.App.Services;
using ChllSeeding.Core;
using ChllSeeding.Core.Api;
using ChllSeeding.Core.Bootstrap;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Games;
using ChllSeeding.Core.Scheduling;
using ChllSeeding.Core.Seeding;
using ChllSeeding.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ChllSeeding.App.ViewModels;

/// <summary>
/// Shared (singleton) view model behind the Seed and Launch tabs. Drives the
/// <see cref="SeedingEngine"/>, mirrors its events into observable UI state, and owns the
/// server-list/stats banner fed by <see cref="AppBootstrapper"/>.
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
    private readonly AutoSeedState _autoSeedState;
    private readonly SseConnectionState _sseState;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer;

    /// <summary>The active seeding session id (from start-session), heartbeat-ed until stop. Null
    /// when no session is open. Analytics only — non-fatal if session creation failed.</summary>
    private string? _sessionId;

    // Anti-spam cooldowns:
    // The "no-op" path (all seeded / error, where IsBusy is already back to Idle so it wouldn't
    // otherwise block a rapid re-click) gets a 30s cooldown; the Seed button gets a 5s anti-spam cooldown.
    private const int SeedAllCooldownSecs = 30;
    private const int SeedRegionCooldownSecs = 5;
    private long _seedAllCooldownUntilMs;
    private long _seedRegionCooldownUntilMs;

    /// <summary>Cap on how long we idle during a scheduled pause before re-polling the directive (so a
    /// huge NextActiveInSecs doesn't strand the loop). The poll re-fetches and resumes when the window opens.</summary>
    private const int ScheduledPauseMaxWaitSecs = 600;

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
        InAppToastService inAppToast,
        AutoSeedState autoSeedState,
        SseConnectionState sseState)
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
        _autoSeedState = autoSeedState;
        _sseState = sseState;

        // Constructed on the UI thread (first page resolve), so this captures the UI queue.
        _dispatcher = DispatcherQueue.GetForCurrentThread();

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
    /// Picks the one-vs-two stop-button layout.</summary>
    [ObservableProperty]
    private bool isSeeding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopSeedingOnly))]
    private bool efficiencyMode;

    /// <summary>Whether "Stop Seeding (keep game)" is offered. Disabled in efficiency mode, where
    /// the game window is minimized and the user should fully stop instead.</summary>
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
    [NotifyPropertyChangedFor(nameof(ServerSwitchHeading))]
    private bool serverSwitchSnoozed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerSwitchHeading))]
    private string serverSwitchTitle = "";

    /// <summary>Overlay heading: the snoozed state gets its own title ("Server Switch Snoozed"),
    /// otherwise the reason-derived <see cref="ServerSwitchTitle"/>.</summary>
    public string ServerSwitchHeading => ServerSwitchSnoozed ? "Server Switch Snoozed" : ServerSwitchTitle;

    [ObservableProperty]
    private string serverSwitchServerName = "";

    [ObservableProperty]
    private string serverSwitchCountdownText = "";

    private long _switchCountdown;
    private long _switchSnoozeRemaining;

    // Auto-seed countdown overlay (a scheduled or missed auto-seed shows a 60s cancellable countdown
    // before launching).
    [ObservableProperty]
    private bool autoseedCountdownActive;

    [ObservableProperty]
    private string autoseedCountdownText = "";

    // Connectivity to the live SSE feed, polled from SseConnectionState on the 1s timer. The UI
    // shows a "Live" indicator while connected and a Reconnect affordance when it's down (stats
    // still update via the HTTP poll fallback meanwhile). Port of the seed_banner Live/Disconnected
    // dot + reconnect_button.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReconnect))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private bool sseConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReconnect))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private int connectionFailures;

    /// <summary>Show the Reconnect button only when the stream is down after a real failure (avoids a
    /// flash during the first connect, where failures is still 0).</summary>
    public bool ShowReconnect => !SseConnected && ConnectionFailures > 0;

    /// <summary>Live-feed indicator label.</summary>
    public string ConnectionStatusText => SseConnected
        ? "Live"
        : (ConnectionFailures > 0 ? "Reconnecting…" : "Connecting…");

    // Seed All cooldown (reactive remaining seconds, ticked by the 1s timer). Drives the
    // "Retry in {n}s" button label.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeedAll))]
    [NotifyPropertyChangedFor(nameof(SeedAllButtonText))]
    private int seedAllCooldownRemaining;

    /// <summary>Seed All is clickable only when not in its post-no-op cooldown.</summary>
    public bool CanSeedAll => SeedAllCooldownRemaining == 0;

    /// <summary>Seed All button label: counts down during the cooldown, else "Seed All".</summary>
    public string SeedAllButtonText => SeedAllCooldownRemaining > 0
        ? $"Retry in {SeedAllCooldownRemaining}s"
        : "Seed All";

    /// <summary>The single ordered server rotation for the current game (region removed).</summary>
    public ObservableCollection<ServerRow> Servers { get; } = [];

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

    /// <summary>Recompute the banner text and which button group is visible.</summary>
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

    /// <summary>Start seeding: the server decides which server to seed and when to rotate; this just
    /// launches and obeys the directive. Replaces the old per-region/Seed-All commands.</summary>
    [RelayCommand]
    private async Task SeedAsync()
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
        // 5s anti-spam cooldown for repeated clicks.
        if (Environment.TickCount64 < _seedRegionCooldownUntilMs)
        {
            return;
        }
        _seedRegionCooldownUntilMs = Environment.TickCount64 + SeedRegionCooldownSecs * 1000L;

        if (!await ConfirmCloseRunningGameAsync().ConfigureAwait(true))
        {
            return;
        }

        ClearSeedError();
        await RunDirectedSeedingAsync(autoSeed: false).ConfigureAwait(true);
    }

    /// <summary>The unified seeding loop: ask the server what to seed (directive), launch it, and obey
    /// the monitor's verdict — relaunch on a switch, end on stop/exhaustion/game-close/user-stop. The
    /// server owns rotation and timing; this is a thin executor. Used by Seed and auto-seed alike.</summary>
    private async Task RunDirectedSeedingAsync(bool autoSeed)
    {
        IsSeeding = true;
        SetStatus(SeedingStatus.Initializing);
        var game = _engine.CurrentGame.Id;

        // Ask the server what (if anything) to seed right now.
        SeedingDirective directive;
        try
        {
            directive = await _api.GetDirectiveAsync(game, null, null).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to fetch seeding directive");
            SetSeedError("Couldn't reach the seeding service. Please try again.");
            IsSeeding = false;
            return;
        }

        if (directive.Target is null)
        {
            IsSeeding = false;
            StartSeedAllCooldown();
            SetSeedError(directive.ScheduledPause
                ? "Seeding is paused — outside the scheduled window."
                : "All servers are seeded — no seeding needed right now.");
            return;
        }

        var index = directive.Target.Index;
        while (true)
        {
            SetStatus(SeedingStatus.Initializing);
            int actual;
            try
            {
                actual = await _engine.StartSeedingAsync(index).ConfigureAwait(true);
            }
            catch (SeedingException e) when (e.Message.Contains("could not open", StringComparison.OrdinalIgnoreCase))
            {
                // The engine spawned the launch watcher; its events drive the rest of the flow.
                SetStatus(SeedingStatus.WaitingForUpdate);
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Seeding failed");
                SetSeedError("Failed to launch the game. Please try again.");
                IsSeeding = false;
                return;
            }

            SetStatus(SeedingStatus.Seeding);
            await StartSessionAsync(actual, autoSeed).ConfigureAwait(true);

            SeedingEngine.MonitorResult result;
            try
            {
                result = await _engine.MonitorSeedAsync(actual, _sessionId).ConfigureAwait(true);
            }
            finally
            {
                await StopSessionAsync("monitor_complete").ConfigureAwait(true);
            }

            if (result.Outcome != SeedingEngine.MonitorOutcome.Directed)
            {
                break; // user stopped or the game closed on its own
            }

            var d = result.Directive;
            if (d is { Action: DirectiveAction.Switch, Target: { } next })
            {
                index = next.Index; // relaunch the server the directive pointed us to
                continue;
            }

            // Stop: exhausted (all seeded) or a scheduled pause — end the run.
            if (d?.ScheduledPause == true)
            {
                _inAppToast.Info("Seeding paused — outside the scheduled window.");
            }
            else
            {
                _inAppToast.Success("Seeding complete — every server is seeded.");
            }
            break;
        }

        if (Status != SeedingStatus.WaitingForUpdate)
        {
            SetStatus(Status == SeedingStatus.Stopping ? SeedingStatus.Stopped : SeedingStatus.Idle);
        }
        IsSeeding = false;
    }

    // ── Auto-seed (scheduled / missed-task triggered) ──────────────────────────

    /// <summary>
    /// Run an auto-seed: a 60s cancellable countdown, then seed whatever the server directs. Invoked on
    /// the UI thread from a <c>--autoseed</c> CLI launch or the missed-task monitor. Port of
    /// <c>run_autoseed</c>, collapsed to a single (region-free) directive-driven run.
    /// </summary>
    public async Task RunAutoseedAsync()
    {
        // Guard: only one auto-seed at a time (mirrors AUTOSEED_IN_PROGRESS).
        if (!_autoSeedState.TryBegin())
        {
            _log.LogInformation("Auto-seed already in progress — ignoring");
            return;
        }
        _autoSeedState.RecordTriggered();

        try
        {
            if (_engine.IsGameRunning || IsBusy)
            {
                _log.LogInformation("Game/seeding already active — skipping auto-seed");
                return;
            }

            // Desktop notification before the countdown so a user away from the keyboard gets a
            // warning before the game launches.
            _toast.Show(Branding.ProductName, "Auto-seed starting in 60 seconds. Click to cancel.");

            // 60s countdown the user can cancel.
            AutoseedCountdownActive = true;
            for (var i = 60; i > 0; i--)
            {
                AutoseedCountdownText = $"Auto-seeding in {i}s…";
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
                if (_autoSeedState.IsCancelled)
                {
                    _log.LogInformation("Auto-seed countdown cancelled by user");
                    return;
                }
            }
            AutoseedCountdownActive = false;

            // Re-check HLL didn't get launched during the countdown.
            if (_engine.IsGameRunning)
            {
                _log.LogInformation("HLL started during countdown — skipping auto-seed");
                return;
            }

            // The server decides what to seed (and whether it's even an active window).
            await RunDirectedSeedingAsync(autoSeed: true).ConfigureAwait(true);
        }
        finally
        {
            AutoseedCountdownActive = false;
            _autoSeedState.End();
        }
    }

    /// <summary>Start the 30s Seed All cooldown (after an "all full"/error no-op). Port of
    /// set_seed_all_cooldown.</summary>
    private void StartSeedAllCooldown()
    {
        _seedAllCooldownUntilMs = Environment.TickCount64 + SeedAllCooldownSecs * 1000L;
        SeedAllCooldownRemaining = SeedAllCooldownSecs;
    }

    /// <summary>Cancel an in-progress auto-seed countdown.</summary>
    [RelayCommand]
    private void CancelAutoseed()
    {
        _autoSeedState.Cancel();
        AutoseedCountdownActive = false;
    }

    // ── Seeding session + heartbeat (analytics; non-fatal) ─────────────────────

    /// <summary>Create a seeding session after a successful launch and start its heartbeat.
    /// Guest/auth required; failures are logged and swallowed.</summary>
    private async Task StartSessionAsync(int index, bool autoSeed = false)
    {
        if (!_auth.IsAuthenticated)
        {
            return;
        }
        try
        {
            var analytics = Analytics.Gather(_config, autoSeed);
            // Forward the first linked Steam ID (persisted by the account VM); null when none linked.
            var steamId = _config.Get<List<string>>("linked_steam_ids")?.FirstOrDefault();
            var resp = await _api.StartSessionAsync(
                _engine.CurrentGame.Id, index, steamId, analytics).ConfigureAwait(true);
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
            await _engine.StartAsync(row.Index).ConfigureAwait(true);
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

    /// <summary>Manually drop and re-establish the live SSE connection. Port of the reconnect button's
    /// request_reconnect().</summary>
    [RelayCommand]
    private void Reconnect() => _sseState.RequestReconnect();

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
        // Title-by-reason mapping: candidate_changed → "Server
        // Switching", server_full → "Server is Full", anything else (incl. time_limit) → "Time
        // Limit Reached". (The engine currently emits only candidate_changed/time_limit, but the
        // full mapping keeps parity if server_full is ever emitted.)
        ServerSwitchTitle = p.Reason switch
        {
            "candidate_changed" => "Server Switching",
            "server_full" => "Server is Full",
            _ => "Time Limit Reached",
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

        var game = _engine.CurrentGame.Id;
        var servers = _servers.GetServers(game);
        Servers.Clear();
        for (var i = 0; i < servers.Count; i++)
        {
            Servers.Add(new ServerRow(i, servers[i]));
        }

        ServersReady = Servers.Count > 0;
    }

    private void ApplyStats(IReadOnlyList<BatchStatsResult> stats)
    {
        var game = _engine.CurrentGame.Id;
        foreach (var row in Servers)
        {
            BatchStatsResult? match = null;
            foreach (var s in stats)
            {
                if (s.Game == game && s.Index == row.Index)
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
        // Mirror the live SSE connection state into observable UI props (cheap, once a second).
        SseConnected = _sseState.Connected;
        ConnectionFailures = _sseState.FailureCount;

        // Tick the Seed All cooldown down to 0 (drives the "Retry in {n}s" button label).
        if (SeedAllCooldownRemaining > 0)
        {
            var remain = _seedAllCooldownUntilMs - Environment.TickCount64;
            SeedAllCooldownRemaining = remain > 0 ? (int)Math.Ceiling(remain / 1000.0) : 0;
        }

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
