using System.Collections.ObjectModel;
using Fullobby.App.Services;
using Fullobby.Core;
using Fullobby.Core.Api;
using Fullobby.Core.Bootstrap;
using Fullobby.Core.Config;
using Fullobby.Core.Games;
using Fullobby.Core.Scheduling;
using Fullobby.Core.Seeding;
using Fullobby.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Fullobby.App.ViewModels;

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
    private readonly AutoSeedService _autoSeed;
    private readonly SseConnectionState _sseState;
    private readonly SeedingStatusCache _statusCache;
    private readonly AccountViewModel _account;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Raised when an auto-seed wake landed in a scheduled pause and the daily wake has been
    /// re-armed to the fleet's (possibly new) window. The app host resleeps/exits if it was launched
    /// specifically for this auto-seed.</summary>
    public event Action? ResleepRequested;

    /// <summary>Raised with a short reason when an auto-seed is refused or abandoned before it seeds
    /// anything. The app host shuts down if this process was launched only to run that seed, so a
    /// blocked scheduled wake doesn't leave a process (and a woken machine) idling until morning.</summary>
    public event Action<string>? AutoseedAbandoned;

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
        AutoSeedService autoSeed,
        SseConnectionState sseState,
        SeedingStatusCache statusCache,
        AccountViewModel account)
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
        _autoSeed = autoSeed;
        _sseState = sseState;
        _statusCache = statusCache;
        _account = account;

        // Constructed on the UI thread (first page resolve), so this captures the UI queue.
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Seed the Power Savings checkbox from the game's remembered choice (per-game key; set
        // directly on the backing field so the load doesn't re-persist through the changed hook).
        efficiencyMode = EfficiencyPreference.IsEnabled(_config, _engine.CurrentGame);

        // Built BEFORE any event subscription below: engine events arrive on the engine's own
        // thread, and a countdown property they set reaches EnsureTimerRunning through its
        // generated changed-hook — which would dereference a not-yet-assigned _timer.
        //
        // The timer now runs only while something is actually counting down (see EnsureTimerRunning
        // and the stop at the end of OnTimerTick). It used to start here and never stop, which meant
        // a 1 Hz UI-thread wake for the life of the process — including the many hours this app
        // spends hidden in the tray, where it defeats timer coalescing and keeps the thread out of
        // deep idle.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;

        _engine.Event += OnEngineEvent;
        _bootstrap.ServersLoaded += OnServersLoaded;
        _bootstrap.ServersLoadFailed += OnServersLoadFailed;
        _live.StatsUpdated += OnStatsUpdated;
        _statusCache.Updated += OnSeedingStatusUpdated;

        // Connection state is event-driven, not polled — see SseConnectionState.Changed.
        _sseState.Changed += OnSseStateChanged;
        SyncSseState();

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

    /// <summary>The Power Savings (efficiency mode) checkbox on the seed surfaces. Two-way bound;
    /// the engine reads the same per-game config key at launch time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopSeedingOnly))]
    private bool efficiencyMode;

    /// <summary>Set while <see cref="SelectGame(GameDefinition)"/> loads a game's remembered choice,
    /// so loading it doesn't persist it straight back.</summary>
    private bool _loadingEfficiencyPreference;

    /// <summary>Persist the checkbox per game so the choice is remembered across sessions.</summary>
    partial void OnEfficiencyModeChanged(bool value)
    {
        if (!_loadingEfficiencyPreference)
        {
            EfficiencyPreference.SetEnabled(_config, _engine.CurrentGame, value);
        }
    }

    /// <summary>Whether the Power Savings checkbox is rendered at all — only for games whose
    /// definition supports efficiency mode (e.g. not Palworld).</summary>
    public bool ShowEfficiencyOption => _engine.CurrentGame.SupportsEfficiencyMode;

    /// <summary>Second line under the seed buttons naming the game a seed starts, e.g.
    /// "(Hell Let Loose: Vietnam)" — the directive picks the game, so the button says which.</summary>
    public string SeedGameLabel => $"({_engine.CurrentGame.DisplayName})";

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

    /// <summary>One-line explanation of why the switch is happening (server-supplied reason).</summary>
    [ObservableProperty]
    private string serverSwitchDetail = "";

    [ObservableProperty]
    private string serverSwitchCountdownText = "";

    private long _switchCountdown;
    private long _switchSnoozeRemaining;

    // Auto-seed "Seed now?" prompt (a scheduled or missed auto-seed shows a 60s confirm overlay
    // before launching: Seed Now / Seed with Power Savings / Cancel, with the countdown visible).
    [ObservableProperty]
    private bool autoseedCountdownActive;

    [ObservableProperty]
    private string autoseedCountdownText = "";

    /// <summary>Context line of the "Seed now?" prompt: the game, plus the directive's current
    /// target server once the best-effort preview fetch lands ("Hell Let Loose — ServerName").</summary>
    [ObservableProperty]
    private string autoseedPromptContext = "";

    /// <summary>What answering Seed Now will do to a game already open ("Hell Let Loose is open —
    /// Seed Now closes it and starts Hell Let Loose: Vietnam"); empty when nothing is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAutoseedSwapNotice))]
    private string autoseedSwapNotice = "";

    public bool ShowAutoseedSwapNotice => AutoseedSwapNotice.Length > 0;

    private void UpdateAutoseedSwapNotice(GameDefinition target, IReadOnlyList<GameDefinition> open) =>
        AutoseedSwapNotice = GameSwap.Plan(target, open).PromptNotice;

    /// <summary>Completed by <see cref="AutoseedChoose"/> when the user answers the "Seed now?"
    /// prompt early (true = with Power Savings). Null outside the countdown.</summary>
    private TaskCompletionSource<bool>? _autoseedChoice;

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

    /// <summary>Local time of the newest applied stats batch, or null before the first one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private DateTime? lastStatsAt;

    /// <summary>Live-feed indicator label. "Live" alone reads as "these numbers are current", but
    /// the counts are a snapshot the API refreshes on its own ~30s cadence — during a seeding surge
    /// a healthy stream is still tens of players behind by the time it lands. Stamping the batch
    /// time says how old the numbers actually are, and does it without a ticking clock: a wall time
    /// only changes when a batch arrives, whereas "updated Ns ago" would need a 1Hz timer in an app
    /// that mostly sits minimized in the tray.</summary>
    public string ConnectionStatusText => SseConnected
        ? (LastStatsAt is { } at ? $"Live · counts as of {at:HH:mm:ss}" : "Live")
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
    /// <summary>Banner text for a directive request that got an HTTP response. Distinguishes
    /// the cases a player can act on (wait out a rate limit, re-sign-in, update) from real
    /// service errors, instead of calling them all "unreachable".</summary>
    private static string DirectiveErrorMessage(ApiException e) => e.StatusCode switch
    {
        null => "Couldn't reach the seeding service. Please try again.",
        System.Net.HttpStatusCode.TooManyRequests =>
            "The seeding service is busy — please try again in a minute.",
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "Your sign-in has expired — sign in again from the Account tab.",
        // "Update required: …" passes through FriendlyError verbatim.
        System.Net.HttpStatusCode.UpgradeRequired => ApiValidation.FriendlyError(e.Message),
        { } s when (int)s >= 500 =>
            "The seeding service hit an error. Please try again shortly.",
        _ => ApiValidation.FriendlyError(e.Message),
    };

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

        // Whether an open game has to close is asked after the directive picks the game (see
        // ResolveGameSwapAsync) — the game in priority may not be the one that's open.
        ClearSeedError();
        await RunDirectedSeedingAsync(autoSeed: false).ConfigureAwait(true);
    }

    /// <summary>Seed via the split button's explicit flyout pair ("true" = with Power Savings):
    /// the click becomes the game's new remembered default, then the normal seed flow runs.</summary>
    [RelayCommand]
    private Task SeedWithChoiceAsync(string withPowerSavings)
    {
        EfficiencyMode = withPowerSavings == "true"; // persisted per game by the changed hook
        return SeedAsync();
    }

    /// <summary>The unified seeding loop: ask the server what to seed (directive), launch it, and obey
    /// the monitor's verdict — relaunch on a switch, end on stop/exhaustion/game-close/user-stop. The
    /// server owns rotation and timing; this is a thin executor. Used by Seed and auto-seed alike.
    /// <paramref name="swapConsented"/>: an auto-seed whose "Seed now?" prompt was answered, which
    /// counts as the player's go-ahead to close a game they have open.</summary>
    private async Task RunDirectedSeedingAsync(
        bool autoSeed, bool swapConsented = false, GameDefinition? consentedSwapTarget = null)
    {
        IsSeeding = true;
        SetStatus(SeedingStatus.Initializing);

        // Ask the server what (if anything) to seed right now, across every game this machine can
        // launch: the server picks the game by network and rotation priority, so a wake set for one
        // game's window seeds whichever game is in priority when it fires.
        SeedingDirective directive;
        try
        {
            directive = await _api.GetDirectiveAsync(
                _engine.CurrentGame.Id, null, null, games: InstalledGames.Ids()).ConfigureAwait(true);
        }
        catch (ApiException e)
        {
            // The service responded — say what actually happened. The old catch-all
            // blamed the network ("couldn't reach") for rate limits, auth expiry,
            // server errors, and even the update-required message alike.
            _log.LogError(e, "Seeding directive request failed ({Status})", e.StatusCode);
            SetSeedError(DirectiveErrorMessage(e));
            IsSeeding = false;
            return;
        }
        catch (Exception e)
        {
            // No HTTP response at all (DNS/connect/timeout) — genuinely unreachable.
            _log.LogError(e, "Failed to reach the seeding service");
            SetSeedError("Couldn't reach the seeding service — check your connection and try again.");
            IsSeeding = false;
            return;
        }

        // Limited-beta hard gate: no network membership → no seeding. Stop and surface the portal.
        if (directive.JoinANetwork)
        {
            IsSeeding = false;
            SetStatus(SeedingStatus.Idle);
            SurfaceJoinANetworkPortal();
            return;
        }

        if (directive.Target is null)
        {
            IsSeeding = false;
            StartSeedAllCooldown();

            // A rotation that is merely waiting — the network is inside its hours but every
            // remaining server is gated on its own later seed window — is a scheduled pause, not
            // an exhausted day. Current APIs say so (scheduled_pause + next_active_in_secs at the
            // server window); older ones said "all exhausted". Either way the day boards carry
            // the window start, so read it from there and name the window — unless the pause is
            // the network's own hours and those open first, which is the plainer message.
            var nextWindowTs = NextServerWindowTs();
            if (nextWindowTs is { } w
                && directive is { ScheduledPause: true, NextActiveInSecs: { } resumeSecs }
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() + resumeSecs < w)
            {
                nextWindowTs = null;
            }
            if (autoSeed && (directive.ScheduledPause || nextWindowTs is not null))
            {
                await RescheduleAndResleepAsync(directive, nextWindowTs).ConfigureAwait(true);
                return;
            }
            SetSeedError(nextWindowTs is { } ts
                ? $"Nothing to seed yet — the next server window opens at {LocalHm(ts)}."
                : directive.ScheduledPause
                    ? "Seeding is paused — outside the scheduled window."
                    : "All servers are seeded — no seeding needed right now.");
            return;
        }

        // A game already open — the other title, or this one started by hand — has to close first,
        // and only with the player's say-so.
        var targetGame = GameCatalog.ById(directive.Target.Game) ?? _engine.CurrentGame;
        var swap = GameSwap.Plan(targetGame, _engine.RunningGames());
        // The nudge's Swap button agreed to a swap *to that game*; if the directive has since moved
        // on (or wants the open game relaunched instead), that isn't what was agreed — ask again.
        var consented = swapConsented
            || (consentedSwapTarget is not null && swap.Kind == GameSwapKind.Swap
                && swap.Target.Id == consentedSwapTarget.Id);
        if (swap.Kind != GameSwapKind.None
            && !await ResolveGameSwapAsync(swap, autoSeed, consented).ConfigureAwait(true))
        {
            IsSeeding = false;
            SetStatus(SeedingStatus.Idle);
            return;
        }

        SelectGame(directive.Target);
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
                // Relaunch the server the directive pointed us to — possibly in the other game (the
                // monitor has already closed this one).
                SelectGame(next);
                index = next.Index;
                continue;
            }

            // Stop: membership revoked mid-run — end the run and surface the portal.
            if (d?.JoinANetwork == true)
            {
                SurfaceJoinANetworkPortal();
                break;
            }

            // Stop: exhausted (all seeded) or a scheduled pause — end the run.
            if (d?.ScheduledPause == true)
            {
                if (autoSeed)
                {
                    await RescheduleAndResleepAsync(d).ConfigureAwait(true);
                    break;
                }
                _inAppToast.Info("Seeding paused — outside the scheduled window.");
            }
            else if (NextServerWindowTs() is { } nextTs)
            {
                // Exhausted for now, not for the day: a windowed server is still to come. The
                // wake set already covers its window (see WakePlanner), so this is just honest.
                _inAppToast.Success(
                    $"Seeding done for now — the next server window opens at {LocalHm(nextTs)}.");
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

    /// <summary>Carry out <paramref name="swap"/> if the player agrees, returning whether the seed may
    /// go ahead. A manual Seed asks (the "Swap games?" dialog) unless the nudge's Swap already did
    /// (<paramref name="consented"/>); an auto-seed closes the open game only when its "Seed now?"
    /// prompt was answered (<paramref name="consented"/>) — unattended, it
    /// never closes a game someone started, and leaves a notification naming the game that needs
    /// seeding instead.</summary>
    private async Task<bool> ResolveGameSwapAsync(GameSwapPlan swap, bool autoSeed, bool consented)
    {
        if (autoSeed && !consented)
        {
            _log.LogInformation("Auto-seed unanswered with {Open} open; left it alone ({Target} not started)",
                swap.ClosingNames, swap.Target.Id);
            _toast.Show(Branding.ProductName, swap.SkippedNotice);
            SetSeedError(swap.SkippedNotice);
            return false;
        }

        if (!autoSeed && !consented)
        {
            SetStatus(SeedingStatus.Idle);
            var confirmed = ConfirmAsync is null
                || await ConfirmAsync(swap.ConfirmMessage, swap.Title).ConfigureAwait(true);
            if (!confirmed)
            {
                _log.LogInformation("Player kept {Open} open; {Target} not started", swap.ClosingNames, swap.Target.Id);
                SetSeedError(swap.DeclinedMessage);
                return false;
            }
            SetStatus(SeedingStatus.Initializing);
        }

        _log.LogInformation("{Kind}: closing {Open} for {Target}", swap.Kind, swap.ClosingNames, swap.Target.Id);
        await _engine.CloseGamesAsync(swap.ToClose).ConfigureAwait(true);
        return true;
    }

    /// <summary>The API flagged this user as having no network membership (the limited-beta gate):
    /// explain it and open the join-a-network portal (onboarding overlay at the network step).</summary>
    private void SurfaceJoinANetworkPortal()
    {
        _inAppToast.Info("Join a seeding network to start seeding — enter your org's join code.");
        _account.OpenNetworkGate();
    }

    /// <summary>The earliest server seed window still ahead today for any game this machine can
    /// launch, from the cached seeding status — or null when none is (or the cache is empty/stale,
    /// in which case the caller has nothing to promise and falls back to the plain messaging).</summary>
    private long? NextServerWindowTs() =>
        ServerWindows.NextOpeningTs(
            _statusCache.GetCached()?.Networks,
            InstalledGames.Ids(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>Put the directive target's game in focus (see <see cref="SelectGame(GameDefinition)"/>).
    /// A game this client doesn't know leaves the focus alone — the server only answers with games
    /// it was asked about, so that would be a server bug, not a reason to launch the wrong game.</summary>
    private void SelectGame(DirectiveTarget target)
    {
        if (GameCatalog.ById(target.Game) is { } game)
        {
            SelectGame(game);
        }
        else if (target.Game.Length > 0)
        {
            _log.LogWarning("Directive named unknown game '{Game}'; staying on {Current}",
                target.Game, _engine.CurrentGame.Id);
        }
    }

    /// <summary>Make <paramref name="game"/> the game in focus: the one the engine launches, the
    /// seed buttons name, whose Power Savings choice applies, and whose servers the board lists.
    /// While idle it follows what a seed would start (<see cref="RefreshSeedGameAsync"/>); during a
    /// run, the game being seeded. A player with one game installed never sees it change.</summary>
    private void SelectGame(GameDefinition game)
    {
        if (ReferenceEquals(_engine.CurrentGame, game))
        {
            return;
        }
        _log.LogInformation("Game in focus: {From} -> {To}", _engine.CurrentGame.Id, game.Id);
        _engine.CurrentGame = game;

        _loadingEfficiencyPreference = true;
        try
        {
            EfficiencyMode = EfficiencyPreference.IsEnabled(_config, game);
        }
        finally
        {
            _loadingEfficiencyPreference = false;
        }
        OnPropertyChanged(nameof(ShowEfficiencyOption));
        OnPropertyChanged(nameof(SeedGameLabel));

        if (_bootstrap.HasServers)
        {
            RebuildServers();
        }
    }

    /// <summary>Minimum gap between idle "which game would Seed start?" directive checks. Status
    /// pushes arrive every ~30s; the answer only changes when a window opens or a rotation moves.</summary>
    private const long SeedGameRefreshMinMs = 60_000;
    private long _seedGameRefreshedAtMs = long.MinValue / 2;

    /// <summary>While idle, ask the directive which game a seed would start and put that game in
    /// focus, so the button's game line is true before it's pressed. Only runs with more than one
    /// launchable game (with one, the answer can't differ). Best-effort: failures leave the focus as
    /// it was — the directive is asked again, authoritatively, when the seed starts.</summary>
    private async Task RefreshSeedGameAsync()
    {
        // Idle, or a game launched from the Launch tab (Running) — that's someone playing, which is
        // exactly when the swap nudge matters. Never mid-seed.
        if (Status is not (SeedingStatus.Idle or SeedingStatus.Stopped or SeedingStatus.Running)
            || IsSeeding || AutoseedCountdownActive || !_auth.IsAuthenticated
            || InstalledGames.Get().Count < 2)
        {
            return;
        }
        var now = Environment.TickCount64;
        if (now - _seedGameRefreshedAtMs < SeedGameRefreshMinMs)
        {
            return;
        }
        _seedGameRefreshedAtMs = now;
        try
        {
            var d = await _api.GetDirectiveAsync(
                _engine.CurrentGame.Id, null, null, games: InstalledGames.Ids()).ConfigureAwait(true);
            if (IsSeeding)
            {
                return;
            }
            // Not while a Launch-tab game runs: the engine watches the game in focus to notice it
            // closing, so moving the focus would end that session under the player.
            if (d.Target is { } target && !IsBusy)
            {
                SelectGame(target);
            }
            EvaluateGameSwapNudge(d.Target is { } t ? GameCatalog.ById(t.Game) : null);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Seed-game preview fetch failed (non-fatal)");
        }
    }

    // ── Swap nudge ─────────────────────────────────────────────────────────────
    // A player with one game open while the directive wants the other gets offered a swap: a
    // desktop notification (they're likely in-game, the app in the tray) and a banner on the Seed
    // page. Offer only — nothing closes until they pick Swap.

    /// <summary>Toast button actions (routed back by the app host).</summary>
    public const string SwapNudgeAcceptAction = "swap-game";
    public const string SwapNudgeSnoozeAction = "swap-not-now";

    private readonly GameSwapNudgePolicy _swapNudgePolicy = new();

    /// <summary>The game the showing nudge offers to swap to; null when none is showing.</summary>
    private GameDefinition? _swapNudgeTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGameSwapNudge))]
    private string gameSwapNudge = "";

    public bool ShowGameSwapNudge => GameSwapNudge.Length > 0;

    /// <summary>Offer a swap to <paramref name="target"/> if a different game is open (and the policy
    /// isn't in a quiet period); clear a stale offer once it no longer applies.</summary>
    private void EvaluateGameSwapNudge(GameDefinition? target)
    {
        var running = _engine.RunningGames();
        if (_swapNudgeTarget is not null
            && (target?.Id != _swapNudgeTarget.Id || running.Count == 0
                || running.Any(g => g.Id == _swapNudgeTarget.Id)))
        {
            ClearGameSwapNudge();
        }

        if (_swapNudgePolicy.Evaluate(target, running, Environment.TickCount64) is not { } plan)
        {
            return;
        }
        _swapNudgeTarget = plan.Target;
        GameSwapNudge = $"{plan.Target.DisplayName} needs seeding. Swap from {plan.ClosingNames}?";
        _log.LogInformation("Swap nudge: {Target} needs seeding, {Open} open", plan.Target.Id, plan.ClosingNames);
        _toast.ShowWithButtons(
            $"{plan.Target.DisplayName} needs seeding",
            $"You're in {plan.ClosingNames}. Swap closes it and starts seeding {plan.Target.DisplayName}.",
            [("Swap", SwapNudgeAcceptAction), ("Not now", SwapNudgeSnoozeAction)]);
    }

    private void ClearGameSwapNudge()
    {
        _swapNudgeTarget = null;
        GameSwapNudge = "";
    }

    /// <summary>The nudge's Swap: close the open game and seed the one that needs it. The click is
    /// the consent — no second dialog, unless the directive has moved on to something else.</summary>
    [RelayCommand]
    private async Task SwapToNeededGameAsync()
    {
        var target = _swapNudgeTarget;
        ClearGameSwapNudge();
        if (target is null || IsSeeding || AutoseedCountdownActive)
        {
            return;
        }
        if (Status == SeedingStatus.Running)
        {
            SetStatus(SeedingStatus.Idle); // the Launch-tab game is about to be closed for the swap
        }
        ClearSeedError();
        await RunDirectedSeedingAsync(autoSeed: false, consentedSwapTarget: target).ConfigureAwait(true);
    }

    /// <summary>The nudge's "Not now": hide it and stay quiet for a while.</summary>
    [RelayCommand]
    private void DismissGameSwapNudge()
    {
        ClearGameSwapNudge();
        _swapNudgePolicy.Snooze(Environment.TickCount64);
    }

    private static string LocalHm(long unixTs) =>
        DateTimeOffset.FromUnixTimeSeconds(unixTs).ToLocalTime().ToString("HH:mm");

    /// <summary>An auto-seed wake landed with nothing to seed yet — outside every scheduled window,
    /// or (<paramref name="nextWindowTs"/> set) inside the hours but ahead of every remaining
    /// server's own window: reconcile the wake set against the directive's fresh config and the
    /// cached day boards, then ask the host to resleep, so the machine learns the moved or later
    /// window and goes back to sleep instead of idling.</summary>
    private async Task RescheduleAndResleepAsync(SeedingDirective directive, long? nextWindowTs = null)
    {
        try
        {
            await _autoSeed.RescheduleFromConfigAsync(directive.Config).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to re-arm the auto-seed wake set for the new window");
        }

        // Describe what is actually armed now (the reconciled set), falling back to the fleet
        // derivation if the store is somehow empty.
        var stored = WakeStore.Load(_config);
        string armed;
        if (stored.Count > 0)
        {
            armed = string.Join(", ", stored.Select(w => $"{w.TimeUtc} UTC"));
        }
        else
        {
            var (h, m) = AutoSeedService.WakeTimeUtc(directive.Config);
            armed = $"{h:D2}:{m:D2} UTC";
        }
        if (nextWindowTs is { } ts)
        {
            _log.LogInformation(
                "Auto-seed ahead of the next server window ({Next}); re-armed for {Armed}, resleeping",
                LocalHm(ts), armed);
            SetSeedError(
                $"Nothing to seed yet — the next server window opens at {LocalHm(ts)}. " +
                $"Re-armed for {armed}; your computer will sleep and wake for it.");
        }
        else
        {
            _log.LogInformation("Auto-seed outside the window; re-armed for {Armed}, resleeping", armed);
            SetSeedError($"Outside the seed window — re-armed for {armed}. Your computer will sleep and wake for it.");
        }
        ResleepRequested?.Invoke();
    }

    // ── Auto-seed (scheduled / missed-task triggered) ──────────────────────────

    /// <summary>
    /// Run an auto-seed: a 60s cancellable countdown, then seed whatever the server directs. Invoked on
    /// the UI thread from an <c>--autoseed[-HHMM]</c> CLI launch or the missed-task monitor.
    /// <paramref name="wakeKey"/> is the "HH:MM" UTC identity of the wake that fired (null for a
    /// legacy wake-less launch) — it keys the per-wake "triggered today" record so the watchdog
    /// doesn't re-fire a wake whose seed already ran, while a later wake the same day still gets its
    /// turn.
    /// </summary>
    public async Task RunAutoseedAsync(string? wakeKey = null)
    {
        // Hard gate: nothing unattended runs while the account isn't ready to seed — mid first-run,
        // needing to sign in again, or holding no network membership. The first two put the
        // onboarding overlay over the whole shell, so the countdown prompt below would be invisible
        // and the desktop notification would be the user's only warning before the game launched;
        // the third has nothing to seed for. Asks SeedingBlocked rather than ShowOnboarding, which
        // covered the network gate only while that gate still hijacked the overlay. Checked before
        // the slot is claimed and before the notification fires, so a blocked auto-seed leaves no
        // trace and disturbs no state. This is the shared backstop — every caller (scheduled-task
        // launch and missed-seed watchdog alike) funnels through here.
        if (_account.SeedingBlocked)
        {
            _log.LogInformation("Auto-seed suppressed — the account isn't ready to seed");
            AutoseedAbandoned?.Invoke("setup isn't finished");
            return;
        }

        // Guard: only one auto-seed at a time (mirrors AUTOSEED_IN_PROGRESS).
        if (!_autoSeedState.TryBegin())
        {
            _log.LogInformation("Auto-seed already in progress — ignoring");
            return;
        }
        _autoSeedState.RecordTriggered(ResolveWakeKey(wakeKey));

        try
        {
            if (IsBusy)
            {
                _log.LogInformation("Seeding already active — skipping auto-seed");
                return;
            }

            // A game already open no longer skips the seed outright: the prompt says what Seed Now
            // would close, and only an answer closes it (see ResolveGameSwapAsync). Unanswered, the
            // player's game is left alone. Provisional until the preview names the target game.
            var openGames = _engine.RunningGames();
            UpdateAutoseedSwapNotice(_engine.CurrentGame, openGames);

            // Desktop notification before the countdown so a user away from the keyboard gets a
            // warning before the game launches — or, with a game open, a chance to swap.
            _toast.Show(Branding.ProductName, openGames.Count == 0
                ? "Auto-seed starting in 60 seconds. Click to cancel."
                : $"Seeding is due, but {string.Join(" and ", openGames.Select(g => g.DisplayName))} is open. " +
                  $"Open {Branding.ProductName} within 60 seconds to swap games.");

            // 60s "Seed now?" prompt. An explicit answer (Seed Now / Seed with Power Savings)
            // starts immediately and becomes the game's new remembered Power Savings default; on
            // timeout the remembered preference applies unchanged, so unattended auto-seeds keep
            // working exactly as before. Cancel keeps its abort semantics.
            _autoseedChoice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AutoseedPromptContext = "";
            _ = FetchAutoseedTargetPreviewAsync(); // best-effort server name for the context line
            AutoseedCountdownActive = true;
            var answered = false;
            for (var i = 60; i > 0; i--)
            {
                AutoseedCountdownText = $"Starting automatically in {i}s…";
                var tick = Task.Delay(TimeSpan.FromSeconds(1));
                var first = await Task.WhenAny(tick, _autoseedChoice.Task).ConfigureAwait(true);
                if (first == _autoseedChoice.Task)
                {
                    // Persisted per game by the changed hook — the explicit click is the new default.
                    EfficiencyMode = await _autoseedChoice.Task.ConfigureAwait(true);
                    answered = true;
                    break;
                }
                if (_autoSeedState.IsCancelled)
                {
                    _log.LogInformation("Auto-seed countdown cancelled by user");
                    return;
                }
                // The gate above reads state that startup is still settling: a scheduled-task launch
                // starts this countdown while RestoreSessionAsync is in flight, and a rejected session
                // (or a revoked network membership) blocks seeding seconds later. Re-check every tick
                // so a gate arming mid-countdown stops the seed instead of launching the game
                // underneath it.
                if (_account.SeedingBlocked)
                {
                    _log.LogInformation("Auto-seed aborted — seeding was blocked during the countdown");
                    AutoseedAbandoned?.Invoke("setup stopped being complete during the countdown");
                    return;
                }
            }
            AutoseedCountdownActive = false;

            // The server decides what to seed (and whether it's even an active window). A game
            // opened by hand during the countdown is caught there too: closed only if answered.
            await RunDirectedSeedingAsync(autoSeed: true, swapConsented: answered).ConfigureAwait(true);
        }
        finally
        {
            AutoseedCountdownActive = false;
            AutoseedSwapNotice = "";
            _autoseedChoice = null;
            _autoSeedState.End();
        }
    }

    /// <summary>A launch that didn't name its wake can only be the legacy single-slot task — while
    /// exactly one wake is stored, that's the one that fired, so its record blocks the watchdog
    /// from running the same seed again. More than one stored wake means the launch really is
    /// unidentifiable (shouldn't happen: multi-wake tasks always pass --autoseed-HHMM), and the
    /// unknown key at least keeps repeat unidentified launches from stacking.</summary>
    private string ResolveWakeKey(string? wakeKey)
    {
        if (wakeKey is not null)
        {
            return wakeKey;
        }
        var stored = WakeStore.Load(_config);
        return stored.Count == 1 ? stored[0].TimeUtc : AutoSeedState.UnknownWakeKey;
    }

    /// <summary>"Seed now?" prompt answered: start the auto-seed immediately, with ("true") or
    /// without ("false") Power Savings. Completes the countdown's choice task.</summary>
    [RelayCommand]
    private void AutoseedChoose(string withPowerSavings) =>
        _autoseedChoice?.TrySetResult(withPowerSavings == "true");

    /// <summary>Best-effort: resolve the directive's current target so the "Seed now?" prompt can
    /// name the server, and put its game in focus so the buttons name the game (and offer Power
    /// Savings only where the game supports it) before the answer. The authoritative directive is
    /// re-fetched when the seed actually starts, so a stale preview is harmless. Failures leave the
    /// buttons naming the game already in focus.</summary>
    private async Task FetchAutoseedTargetPreviewAsync()
    {
        try
        {
            var d = await _api.GetDirectiveAsync(
                _engine.CurrentGame.Id, null, null, games: InstalledGames.Ids()).ConfigureAwait(true);
            if (AutoseedCountdownActive && d.Target is { } target)
            {
                SelectGame(target);
                UpdateAutoseedSwapNotice(_engine.CurrentGame, _engine.RunningGames());
                if (target.Server.Name.Length > 0)
                {
                    AutoseedPromptContext = target.Server.Name;
                }
            }
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Auto-seed target preview fetch failed (non-fatal)");
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
            var analytics = Analytics.Gather(_config, _engine.CurrentGame, autoSeed);
            // Forward the linked Steam ID (persisted by the account VM); null when none linked.
            var steamId = _config.GetString("linked_steam_id");
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

            case SeedingEvent.RejoinRestarting r:
                // The splash-bypass events fired by the relaunch drive the visual state; this is just
                // an explanation of why the game is restarting.
                _inAppToast.Info(r.MaxAttempts > 0
                    ? $"Connection stalled — restarting to rejoin {r.ServerName} (attempt {r.Attempt} of {r.MaxAttempts})…"
                    : $"Connection stalled — restarting to rejoin {r.ServerName}…");
                break;

            case SeedingEvent.SeedingAborted a:
                // The engine already closed the game; explain why and drop back to idle.
                ResetSwitchOverlay();
                SplashBypassActive = false;
                SetSeedError(a.Reason switch
                {
                    "server_full" =>
                        "The server filled up before you could get in — no more seeding needed there. Pick another server or try again shortly.",
                    "server_offline" =>
                        "The server went offline, so seeding stopped. Try another server.",
                    "join_failed" =>
                        "Couldn't connect to the server after several tries. It may be having issues — try again or pick another server.",
                    "validation_failed" =>
                        "You dropped off the server and couldn't be re-verified, so seeding stopped.",
                    _ => "The seeding service ended this session. Please try again.",
                });
                break;
        }
    }

    private void ShowSwitchOverlay(SeedingEvent.ServerSwitchPending p)
    {
        ServerSwitchServerName = p.ServerName;
        // Title + one-line detail derived from the server-supplied switch reason.
        (ServerSwitchTitle, ServerSwitchDetail) = p.Reason switch
        {
            SwitchReason.Seeded =>
                ("Server Seeded", $"Your server reached its seeding target — moving to {p.ServerName}."),
            SwitchReason.Unavailable =>
                ("Server Unavailable", $"Your server went offline — moving to {p.ServerName}."),
            SwitchReason.HigherPriorityReady =>
                ("Higher-Priority Server", $"{p.ServerName}'s staff confirmed ready — switching there."),
            SwitchReason.NetworkTransition =>
                ("Next Network", $"Your current network is done for now — moving to {p.ServerName} in your next network."),
            // RotationAdvanced or null (older API): generic advance.
            _ => ("Server Switching", $"Moving to {p.ServerName}."),
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

        // Paint ready-standing badges from the last cached status (if still fresh).
        var cached = _statusCache.GetCached();
        if (cached is not null)
        {
            ApplyDayStatuses(cached);
        }
    }

    private void OnSeedingStatusUpdated(SeedingStatusResponse status) =>
        _dispatcher.TryEnqueue(() =>
        {
            ApplyDayStatuses(status);
            _ = RefreshSeedGameAsync();
        });

    /// <summary>Apply per-server ready standing to the server rows. The status is per network now:
    /// flatten each network's day board (by db_id — a server belongs to exactly one network) into
    /// one list matched to the rows by short-name, and set the board's network group headers
    /// (shown only when more than one network has servers, to avoid clutter).</summary>
    private void ApplyDayStatuses(SeedingStatusResponse status)
    {
        var game = _engine.CurrentGame.Id;

        var days = new List<ServerDayStatus>();
        var networkByDbId = new Dictionary<long, string>();
        var networksWithServers = 0;
        foreach (var n in status.Networks)
        {
            var networkDays = game == "hllv" ? n.HllvDay : n.HllDay;
            if (networkDays.Count == 0)
            {
                continue;
            }
            networksWithServers++;
            var label = n.DisplayName is { Length: > 0 } d ? d : n.NetworkTag;
            foreach (var day in networkDays)
            {
                days.Add(day);
                networkByDbId[day.DbId] = label;
            }
        }

        var showHeaders = networksWithServers > 1;
        string? previousNetwork = null;
        foreach (var row in Servers)
        {
            ServerDayStatus? match = null;
            foreach (var d in days)
            {
                if (d.ShortName == row.ShortName)
                {
                    match = d;
                    break;
                }
            }
            row.ApplyDayStatus(match);

            // Header on the first row of each contiguous network group.
            string? network = null;
            if (match is not null && networkByDbId.TryGetValue(match.DbId, out var label))
            {
                network = label;
            }
            row.SetNetworkGroup(showHeaders && network is not null && network != previousNetwork ? network : null);
            if (network is not null)
            {
                previousNetwork = network;
            }
        }
    }

    private void ApplyStats(IReadOnlyList<BatchStatsResult> stats)
    {
        LastStatsAt = _live.LastUpdateUtc?.ToLocalTime();

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

    /// <summary>True while some countdown still needs a per-second tick. The countdowns here are
    /// display-only (the engine owns the real timing), so the worst a mis-gate can do is freeze a
    /// label — it cannot desync seeding, the splash bypass, or a server switch.</summary>
    private bool NeedsTicking =>
        SeedAllCooldownRemaining > 0 || SplashBypassActive || ServerSwitchActive;

    /// <summary>Start the countdown timer if anything needs it. Safe to call from any thread:
    /// engine events reach these properties from the seeding engine's own thread, and a
    /// DispatcherTimer must be started on the UI thread.</summary>
    private void EnsureTimerRunning()
    {
        if (!NeedsTicking)
        {
            return;
        }
        if (_dispatcher.HasThreadAccess)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!_timer.IsEnabled && NeedsTicking)
                {
                    _timer.Start();
                }
            });
        }
    }

    // Any path that switches one of these on starts the timer, so no caller has to remember to.
    partial void OnSplashBypassActiveChanged(bool value) => EnsureTimerRunning();

    partial void OnServerSwitchActiveChanged(bool value) => EnsureTimerRunning();

    partial void OnSeedAllCooldownRemainingChanged(int value) => EnsureTimerRunning();

    private void OnSseStateChanged() => _dispatcher.TryEnqueue(SyncSseState);

    /// <summary>Copy the shared connection state into the observable properties the Seed page binds
    /// (the Live dot, the status text, the Reconnect button). Must run on the UI thread.</summary>
    private void SyncSseState()
    {
        SseConnected = _sseState.Connected;
        ConnectionFailures = _sseState.FailureCount;
    }

    private void OnTimerTick(object? sender, object e)
    {
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

        // Nothing left to count down — go back to sleep until something restarts us.
        if (!NeedsTicking)
        {
            _timer.Stop();
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
