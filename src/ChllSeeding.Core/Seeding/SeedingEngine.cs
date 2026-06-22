using System.Diagnostics;
using ChllSeeding.Core.Api;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Games;
using ChllSeeding.Core.Native;
using ChllSeeding.Core.Servers;
using ChllSeeding.Core.Tools;
using Microsoft.Extensions.Logging;
using Windows.Win32;

namespace ChllSeeding.Core.Seeding;

/// <summary>
/// Drives a full seeding session: launch the game through Steam, bypass the splash/EAC,
/// then monitor server population and rotate/stop as needed. Port of the core state machine
/// in <c>src-rust/src/backend/seeding.rs</c> (process-global atomics/tokio-tasks →
/// DI-singleton instance state + Task-based background work). Runs entirely off the UI thread;
/// the UI subscribes to <see cref="Event"/> for countdowns, banners, and switch prompts.
/// </summary>
public sealed class SeedingEngine : IDisposable
{
    // ── Native launch/splash timing constants (seconds) — client mechanics, not
    //    seeding policy. Seeding-policy timings (stagger, countdown, monitor cadence,
    //    max session, snooze) now come from the server directive + SeedingConfig.
    public const long SplashBypassMinSecs = 10;
    public const long SplashBypassMaxSecs = 60;

    public const long EacLaunchTimeoutSecs = 180;
    public const long WindowWaitTimeoutSecs = 60;
    public const long GameOpenFirstRetrySecs = 60;
    public const long GameOpenSecondRetrySecs = 120;
    public const long GameOpenTimeoutSecs = 180;

    public const long PostKillWaitSecs = 20;

    public const long LaunchWatcherTimeoutSecs = 600; // 10 min

    public const long StatusLogIntervalSecs = 300;

    private readonly ILogger<SeedingEngine> _log;
    private readonly SeedingState _state;
    private readonly ProcessMonitor _process;
    private readonly WindowFocus _window;
    private readonly Win11Input _win11;
    private readonly SteamLauncher _steam;
    private readonly HllConfigBackupService _backup;
    private readonly ServerStore _servers;
    private readonly SeedingStatusCache _statusCache;
    private readonly SeedingApiClient _api;
    private readonly ConfigService _config;
    private readonly SeedingConfigProvider _configProvider;
    private readonly KeepAwake _keepAwake;

    private readonly CancellationTokenSource _lifetime = new();

    // Edge-triggered wake for the monitor's interruptible sleep (SSE reconnect or stop).
    private readonly SemaphoreSlim _monitorSignal = new(0, 1);

    // The game currently being seeded (defaults to HLL when idle).
    private volatile GameDefinition _currentGame = GameCatalog.Hll;

    // Once-per-session config backup guard (0 = not yet, 1 = backed up). Interlocked.
    private int _configBackedUp;

    // Single active launch watcher guard (0 = none, 1 = active). Interlocked.
    private int _launchWatcherActive;

    public SeedingEngine(
        ILogger<SeedingEngine> log,
        SeedingState state,
        ProcessMonitor process,
        WindowFocus window,
        Win11Input win11,
        SteamLauncher steam,
        HllConfigBackupService backup,
        ServerStore servers,
        SeedingStatusCache statusCache,
        SeedingApiClient api,
        ConfigService config,
        SeedingConfigProvider configProvider,
        KeepAwake keepAwake)
    {
        _log = log;
        _state = state;
        _process = process;
        _window = window;
        _win11 = win11;
        _steam = steam;
        _backup = backup;
        _servers = servers;
        _statusCache = statusCache;
        _api = api;
        _config = config;
        _configProvider = configProvider;
        _keepAwake = keepAwake;
    }

    /// <summary>Backend → UI events for the active seeding session. Raised from background threads;
    /// subscribers must marshal to the UI thread themselves.</summary>
    public event Action<SeedingEvent>? Event;

    /// <summary>The shared stop/snooze/switch coordination state (also the snooze/confirm command surface).</summary>
    public SeedingState State => _state;

    /// <summary>The game currently being seeded.</summary>
    public GameDefinition CurrentGame
    {
        get => _currentGame;
        set => _currentGame = value;
    }

    private void Emit(SeedingEvent e) => Event?.Invoke(e);

    // ── Public command surface (port of the pub fns in seeding.rs) ─────────────

    /// <summary>Start seeding the server at index <paramref name="serverNumber"/> in the current
    /// game's rotation: launch the game and run the splash bypass. Returns the server index. On
    /// launch failure, restores settings, spawns the launch watcher, and rethrows. The caller then
    /// runs <see cref="MonitorSeedAsync"/>.</summary>
    public Task<int> StartSeedingAsync(int serverNumber, CancellationToken ct = default) =>
        StartSeedingImplAsync(serverNumber, ct);

    /// <summary>Direct launch + focus (no seeding monitor, no efficiency mode). Port of <c>start_impl</c>.</summary>
    public Task StartAsync(int serverNumber, CancellationToken ct = default) =>
        StartImplAsync(serverNumber, ct);

    /// <summary>Run the directive-driven monitor loop for an already-launched seed: poll the server
    /// for what to do (stay / switch / stop) and obey. <paramref name="sessionId"/> lets the server
    /// enforce the max-session limit.</summary>
    public Task<MonitorResult> MonitorSeedAsync(int serverNumber, string? sessionId = null, CancellationToken ct = default) =>
        MonitorSeedImplAsync(serverNumber, sessionId, ct);

    /// <summary>Whether the game currently being seeded is running. UI guard for the
    /// "game already running" confirmation before (re)starting a seed or launch.</summary>
    public bool IsGameRunning => _process.IsGameRunning(_currentGame);

    /// <summary>Kill the current game and wait (up to <paramref name="maxWaitSecs"/>) for it to exit.
    /// Used by the UI when the user confirms closing a running game before seeding/launching;
    /// mirrors the kill-and-poll loop in the Rust seed/launch components.</summary>
    public async Task KillGameAndWaitAsync(int maxWaitSecs = 20, CancellationToken ct = default)
    {
        var game = _currentGame;
        KillGameProcess(game);
        for (var i = 0; i < maxWaitSecs * 2; i++)
        {
            if (!_process.IsGameRunning(game))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Snooze a pending server switch for the given duration (clamped to the server-configured
    /// snooze bounds, falling back to the baked-in defaults).</summary>
    public void SnoozeServerSwitch(long durationSecs)
    {
        var cfg = _configProvider.Current;
        var clamped = Math.Clamp(durationSecs, cfg.SnoozeMinSecs, cfg.SnoozeMaxSecs);
        _log.LogInformation("Server switch snoozed for {Secs}s by user", clamped);
        _state.SetSwitchSnooze(clamped);
    }

    /// <summary>Confirm a pending server switch immediately (skip the countdown).</summary>
    public void ConfirmServerSwitch()
    {
        _log.LogInformation("Server switch confirmed immediately by user");
        _state.RequestSwitchNow();
    }

    /// <summary>Wake the monitor loop's sleep (called on SSE reconnect so it re-checks the candidate
    /// with fresh data). Edge-triggered: a wake pending while not sleeping coalesces to one.</summary>
    public void NotifyMonitor()
    {
        try { _monitorSignal.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    /// <summary>Stop seeding and kill the game. Port of <c>stop_seeding</c>.</summary>
    public async Task StopSeedingAsync()
    {
        _log.LogInformation("Stopping Seeding");
        _state.RequestStop();
        NotifyMonitor();
        CancelLaunchWatcher();

        if (_process.IsGameLoading(_currentGame))
        {
            _log.LogInformation("Waiting for HLL to finish loading");
            for (var i = 0; i < 60; i++)
            {
                if (_process.IsGameRunning(_currentGame) && !_process.IsGameLoading(_currentGame))
                {
                    _log.LogInformation("HLL is done loading, waiting 2 seconds so Easy Anti-Cheat doesn't get stuck");
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    KillGameProcess();
                    await GuardConfigAfterCloseAsync().ConfigureAwait(false);
                    _backup.RestoreAfterSeeding();
                    _keepAwake.Release();
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            _log.LogInformation("HLL must have stopped on its own, aborting");
            await GuardConfigAfterCloseAsync().ConfigureAwait(false);
            _backup.RestoreAfterSeeding();
            _keepAwake.Release();
            return;
        }

        KillGameProcess();
        await GuardConfigAfterCloseAsync().ConfigureAwait(false);
        _backup.RestoreAfterSeeding();
        _keepAwake.Release();
        _log.LogInformation("Seeding stopped. Shutdown Complete.");
    }

    /// <summary>Stop the seeding monitor but leave the game running. Port of <c>stop_seeding_only</c>.</summary>
    public Task StopSeedingOnlyAsync()
    {
        _log.LogInformation("Stopping seeding (keeping game running)");
        _state.RequestStop();
        NotifyMonitor();
        CancelLaunchWatcher();
        _backup.RestoreAfterSeeding();
        _window.InvalidateCache();
        _keepAwake.Release();
        _log.LogInformation("Seeding stopped. Game still running.");
        return Task.CompletedTask;
    }

    /// <summary>On app exit: if efficiency mode is applied, kill the game and restore the user's real
    /// settings so the game isn't left degraded. Port of <c>cleanup_efficiency_on_exit</c>.</summary>
    public async Task CleanupEfficiencyOnExitAsync()
    {
        if (!_backup.IsEfficiencyModeApplied)
        {
            return;
        }

        var game = _currentGame;
        if (!_process.IsGameRunning(game))
        {
            _backup.RestoreAfterSeeding();
            _keepAwake.Release();
            return;
        }

        _log.LogInformation("Efficiency mode active on exit — killing game and restoring settings");
        KillGameProcess(game);

        for (var i = 0; i < 20; i++)
        {
            if (!_process.IsGameRunning(game))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        _backup.RestoreAfterSeeding();
        _keepAwake.Release();
    }

    // ── Config backup helpers (port of the CONFIG BACKUP section) ──────────────

    /// <summary>If HLL reset its config to defaults, restore from backup; otherwise back it up once
    /// per session. Port of <c>check_and_restore_config</c>.</summary>
    public void CheckAndRestoreConfig()
    {
        if (_backup.IsConfigOverwritten())
        {
            _log.LogInformation("Config is reverted to default, restoring");
            _backup.RestoreConfig();
        }
        else if (Interlocked.CompareExchange(ref _configBackedUp, 1, 0) == 0)
        {
            _log.LogInformation("Config is not overwritten. Performing a backup");
            _backup.BackupConfig();
        }
    }

    /// <summary>After the game exits, wait for late config writes then restore if HLL reset the
    /// config on its way out. Port of <c>guard_config_after_close</c>.</summary>
    private async Task GuardConfigAfterCloseAsync()
    {
        if (Volatile.Read(ref _configBackedUp) == 0)
        {
            return; // nothing backed up to restore from
        }
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _backup.InvalidateConfigCache();
        if (_backup.IsConfigOverwritten())
        {
            _log.LogInformation("Game reset config on exit, restoring from backup");
            _backup.RestoreConfig();
        }
    }

    /// <summary>Clear the stop flag and re-arm the once-per-session backup for a new session.
    /// Port of <c>clear_stop</c> (which also calls <c>reset_config_backup_flag</c>).</summary>
    private void ClearStopForNewSession()
    {
        _state.ClearStop();
        Volatile.Write(ref _configBackedUp, 0);
    }

    // ── Kill helpers ───────────────────────────────────────────────────────────

    /// <summary>Restore config if needed, kill the current game's processes, invalidate the window
    /// cache. Port of <c>kill_hll_process</c> / <c>kill_game_process</c>.</summary>
    private void KillGameProcess() => KillGameProcess(_currentGame);

    private void KillGameProcess(GameDefinition game)
    {
        CheckAndRestoreConfig();
        _process.KillGameProcesses(game);
        _window.InvalidateCache();
    }

    // ── Launch / open (port of open_game + the steam.rs orchestration) ─────────

    /// <summary>Launch the game through Steam and connect, applying efficiency mode first when
    /// requested+enabled. The mouse-nudge and config-backup that lived in <c>steam.rs::open_game</c>
    /// are orchestrated here (SteamLauncher does launch mechanics only).</summary>
    private async Task OpenGameAsync(ServerInfo server, bool applyEfficiency, CancellationToken ct)
    {
        CheckAndRestoreConfig();

        if (applyEfficiency && _currentGame.SupportsEfficiencyMode && _config.GetBool("efficiency_mode"))
        {
            _log.LogInformation("Efficiency mode enabled for seeding - applying low graphics settings");
            _backup.ApplyEfficiencySettings();
        }

        MouseNudge();
        await _steam.OpenGameAsync(_currentGame, server.Ip, ct).ConfigureAwait(false);
    }

    /// <summary>Move the cursor away before launch, mirroring the enigo mouse-move in
    /// <c>steam.rs::open_game</c> (best-effort; some launch overlays only take focus after a move).</summary>
    private void MouseNudge()
    {
        try
        {
            PInvoke.SetCursorPos(700, 700);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Mouse-nudge before launch failed, continuing");
        }
    }

    // ── Splash bypass (port of focus_hll) ──────────────────────────────────────

    private async Task FocusHllAsync(CancellationToken ct)
    {
        // Default from the server config; a local user override still wins.
        var bypassDuration = (long)_configProvider.Current.SplashBypassSecs;
        var configured = _config.GetString("splash_bypass_duration");
        if (configured is not null && long.TryParse(configured, out var parsed))
        {
            bypassDuration = parsed;
        }
        bypassDuration = Math.Clamp(bypassDuration, SplashBypassMinSecs, SplashBypassMaxSecs);

        _log.LogInformation("Splash bypass: watching for EAC bootstrapper to finish");

        // Phase 1: wait for the EAC launcher to finish AND the game process to be running.
        var waitStart = Stopwatch.StartNew();
        var sawEac = false;
        var eacIntervalMs = 1000;
        while (true)
        {
            if (_state.IsStopRequested)
            {
                _log.LogInformation("Stop requested during splash bypass phase 1 (EAC wait)");
                return;
            }

            var (eacRunning, hllRunning) = _process.CheckGameLaunchProcesses(_currentGame);

            if (eacRunning)
            {
                sawEac = true;
                _log.LogInformation("EAC bootstrapper running, waiting for it to finish...");
                eacIntervalMs = 1000;
            }
            else
            {
                eacIntervalMs = Math.Min(eacIntervalMs + 500, 3000);
            }

            if (!eacRunning && hllRunning)
            {
                _log.LogInformation(sawEac
                    ? $"EAC bootstrapper finished after {waitStart.Elapsed.TotalSeconds:F0}s, HLL process started"
                    : $"HLL process running (EAC already finished) after {waitStart.Elapsed.TotalSeconds:F0}s");
                break;
            }

            if (waitStart.Elapsed.TotalSeconds > EacLaunchTimeoutSecs)
            {
                _log.LogInformation("Timeout waiting for game launch after {Secs}s, aborting splash bypass", EacLaunchTimeoutSecs);
                Emit(new SeedingEvent.SplashBypassTimeout("Game did not launch within timeout"));
                return;
            }
            await Task.Delay(eacIntervalMs, ct).ConfigureAwait(false);
        }

        // Phase 2: wait for the HLL window to appear.
        _log.LogInformation("Waiting for HLL window to appear...");
        var windowWaitStart = Stopwatch.StartNew();
        var windowIntervalMs = 250;
        while (true)
        {
            if (_state.IsStopRequested)
            {
                _log.LogInformation("Stop requested during splash bypass phase 2 (window wait)");
                return;
            }
            if (_window.HasHllWindow())
            {
                _log.LogInformation("HLL window found after {Secs}s", $"{windowWaitStart.Elapsed.TotalSeconds:F0}");
                break;
            }
            if (windowWaitStart.Elapsed.TotalSeconds > WindowWaitTimeoutSecs)
            {
                _log.LogInformation("HLL window not found after {Secs}s, aborting splash bypass", WindowWaitTimeoutSecs);
                Emit(new SeedingEvent.SplashBypassTimeout("Game window did not appear"));
                return;
            }
            await Task.Delay(windowIntervalMs, ct).ConfigureAwait(false);
            windowIntervalMs = Math.Min(windowIntervalMs + 250, 1000);
        }

        Emit(new SeedingEvent.SplashBypassStarted(bypassDuration));

        // Phase 3: spam Escape (first 30s) + F13 via PostMessage, plus a UIA/SendInput fallback on Win11.
        var isWin11 = Win11Input.IsWindows11OrLater();
        if (isWin11)
        {
            _log.LogInformation("Windows 11 detected - will use additional input methods");
        }

        var bypassStart = Stopwatch.StartNew();
        var attempt = 0;
        while (bypassStart.Elapsed.TotalSeconds < bypassDuration)
        {
            if (_state.IsStopRequested)
            {
                _log.LogInformation("Stop requested during splash bypass phase 3 (key sending)");
                return;
            }

            attempt++;
            var elapsed = bypassStart.Elapsed.TotalSeconds;
            var sendEscape = elapsed < 30;
            var keyIntervalSecs = elapsed < 15 ? 1 : elapsed < 30 ? 2 : 3;

            _log.LogDebug("Splash bypass attempt {Attempt} ({Elapsed:F0}s / {Total}s) [escape={Escape}, interval={Interval}s]",
                attempt, elapsed, bypassDuration, sendEscape, keyIntervalSecs);

            // PostMessage path (sync P/Invoke with internal sleeps — run off the loop thread).
            var posted = await Task.Run(() => _window.SendKeysToHll(sendEscape), ct).ConfigureAwait(false);
            if (!posted)
            {
                _log.LogInformation("HLL window not found, game may have been closed");
                break;
            }

            if (isWin11)
            {
                await Task.Run(() => _win11.TrySendKeysViaUia(sendEscape), ct).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(keyIntervalSecs), ct).ConfigureAwait(false);
        }

        // Phase 4: last-resort focus attempts.
        _log.LogInformation("Final focus attempt with AttachThreadInput");
        await Task.Run(() => _window.ForceFocusAndSendKey(), ct).ConfigureAwait(false);

        if (isWin11)
        {
            _log.LogInformation("Final attempt with UI Automation (Win11)");
            await Task.Run(() => _win11.TryAllInputMethods(false), ct).ConfigureAwait(false);
        }

        Emit(new SeedingEvent.SplashBypassComplete());

        if (_config.GetBool("efficiency_mode"))
        {
            _log.LogInformation("Efficiency mode enabled - minimizing HLL window");
            if (_window.MinimizeHllWindow())
            {
                _log.LogInformation("HLL window minimized");
            }
            else
            {
                _log.LogInformation("HLL window not found for minimizing");
            }
        }

        _log.LogInformation("Splash screen bypass complete");
    }

    // ── Start / launch inner (port of start_seeding_inner) ─────────────────────

    private async Task StartSeedingInnerAsync(ServerInfo server, CancellationToken ct)
    {
        _log.LogInformation("Starting Seeding {Name}", server.Name);
        var attemptedOpen = Stopwatch.StartNew();

        if (_process.IsGameLoading(_currentGame) || _process.IsGameRunning(_currentGame))
        {
            _log.LogInformation("HLL is already running, exiting script - {Name}", server.Name);
            return;
        }

        await OpenGameAsync(server, applyEfficiency: true, ct).ConfigureAwait(false);

        var foundHllRunning = false;
        var firstRetry = false;
        var secondRetry = false;

        while (!foundHllRunning)
        {
            if (_state.IsStopRequested)
            {
                _log.LogInformation("Stop requested during game launch - {Name}", server.Name);
                return;
            }

            if (_process.IsGameRunning(_currentGame))
            {
                _log.LogInformation("Found HLL running - {Name}", server.Name);
                foundHllRunning = true;
            }

            if (!foundHllRunning && !_process.IsGameLoading(_currentGame))
            {
                if (attemptedOpen.Elapsed.TotalSeconds > GameOpenFirstRetrySecs && !firstRetry)
                {
                    if (_state.IsStopRequested) return;
                    _log.LogInformation("HLL not found open, trying again - {Name}", server.Name);
                    try { await OpenGameAsync(server, applyEfficiency: false, ct).ConfigureAwait(false); }
                    catch (Exception e) { _log.LogInformation(e, "Failed to retry opening HLL"); }
                    firstRetry = true;
                }

                if (attemptedOpen.Elapsed.TotalSeconds > GameOpenSecondRetrySecs && !secondRetry)
                {
                    if (_state.IsStopRequested) return;
                    _log.LogInformation("HLL not found open, final retry - {Name}", server.Name);
                    try { await OpenGameAsync(server, applyEfficiency: false, ct).ConfigureAwait(false); }
                    catch (Exception e) { _log.LogInformation(e, "Failed to retry opening HLL"); }
                    secondRetry = true;
                }

                if (attemptedOpen.Elapsed.TotalSeconds > GameOpenTimeoutSecs)
                {
                    _log.LogInformation("Error: HLL could not open - {Name}", server.Name);
                    throw new SeedingException("Error: HLL could not open");
                }
            }

            if (foundHllRunning)
            {
                if (_state.IsStopRequested) return;
                await FocusHllAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
        }

        _log.LogInformation("Seeding started. Startup Complete. - {Name}", server.Name);
    }

    // ── Public-command implementations ─────────────────────────────────────────

    /// <summary>Why a monitor loop returned, so the caller (ViewModel) can orchestrate what's next.</summary>
    public enum MonitorOutcome
    {
        /// <summary>The user stopped seeding.</summary>
        UserStopped,
        /// <summary>The game closed on its own.</summary>
        GameClosed,
        /// <summary>The server directed an action (switch/stop/pause) — see <see cref="MonitorResult.Directive"/>.</summary>
        Directed,
    }

    /// <summary>Result of a monitor loop. When <see cref="Outcome"/> is <see cref="MonitorOutcome.Directed"/>,
    /// <see cref="Directive"/> is the final directive (Switch → relaunch its target; Stop with
    /// <c>ScheduledPause</c> → idle then resume; Stop otherwise → done for now).</summary>
    public readonly record struct MonitorResult(MonitorOutcome Outcome, SeedingDirective? Directive);

    private async Task<int> StartSeedingImplAsync(int serverNumber, CancellationToken ct)
    {
        ClearStopForNewSession();
        CancelLaunchWatcher();

        if (_process.IsGameRunning(_currentGame))
        {
            // The monitor will run against the already-running game, so keep the system awake.
            _keepAwake.Acquire();
            _log.LogInformation("HLL is already running, exiting start_seeding");
            return serverNumber;
        }

        // Resolve the server BEFORE acquiring keep-awake: a no-server throw here would otherwise leak
        // the SetThreadExecutionState hold (this method throws past MonitorSeed's release finally).
        var server = _servers.GetServer(_currentGame.Id, serverNumber)
            ?? throw new SeedingException($"No server at index {serverNumber}");

        // Committed to launching — hold the system awake (released on monitor exit / stop / cleanup).
        _keepAwake.Acquire();

        try
        {
            await StartSeedingInnerAsync(server, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _backup.RestoreAfterSeeding();
            SpawnLaunchWatcher(server, serverNumber);
            throw;
        }

        _log.LogInformation("Seeding started. Monitor process will now run. - {Name}", server.Name);
        return serverNumber;
    }

    private async Task StartImplAsync(int serverNumber, CancellationToken ct)
    {
        CancelLaunchWatcher();
        var server = _servers.GetServer(_currentGame.Id, serverNumber)
            ?? throw new SeedingException($"No server at index {serverNumber}");

        // Direct launch: do NOT apply efficiency mode.
        await OpenGameAsync(server, applyEfficiency: false, ct).ConfigureAwait(false);
        await FocusHllAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Launch complete - {Name}", server.Name);
    }

    private async Task<MonitorResult> MonitorSeedImplAsync(int serverNumber, string? sessionId, CancellationToken ct)
    {
        _log.LogInformation("Monitoring seed running (index {Index})", serverNumber);

        var server = _servers.GetServer(_currentGame.Id, serverNumber)
            ?? throw new SeedingException($"No server at index {serverNumber}");

        try
        {
            return await MonitorLoopAsync(server, serverNumber, sessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            // Always restore the user's real settings (also resets the efficiency-applied flag) and
            // drop the keep-awake hold. Both idempotent. Deliberate divergence from Rust (which defers
            // restore to the caller): every monitor return is terminal with the game killed or closing.
            _backup.RestoreAfterSeeding();
            _keepAwake.Release();
        }
    }

    // ── Monitor loop (directive-driven) ────────────────────────────────────────

    /// <summary>Fetch the current directive and refresh the shared config from it, or null on failure.</summary>
    private async Task<SeedingDirective?> TryGetDirectiveAsync(int? currentIndex, string? sessionId, CancellationToken ct)
    {
        try
        {
            var d = await _api.GetDirectiveAsync(_currentGame.Id, currentIndex, sessionId, ct).ConfigureAwait(false);
            _configProvider.Update(d.Config);
            return d;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Directive fetch failed; will keep seeding and retry");
            return null;
        }
    }

    /// <summary>Interruptible sleep that wakes early on an SSE-reconnect/stop notification.</summary>
    private async Task InterruptibleSleepAsync(int secs, CancellationToken ct)
    {
        if (secs <= 0)
        {
            return;
        }
        if (await _monitorSignal.WaitAsync(TimeSpan.FromSeconds(secs), ct).ConfigureAwait(false))
        {
            _log.LogDebug("Monitor woken early (SSE reconnect or stop)");
        }
    }

    /// <summary>Poll the server directive and obey it until the game closes, the user stops, or the
    /// server tells us to switch/stop. All decision/timing logic lives server-side; this loop is a
    /// thin executor.</summary>
    private async Task<MonitorResult> MonitorLoopAsync(
        ServerInfo server, int index, string? sessionId, CancellationToken ct)
    {
        while (true)
        {
            if (_state.IsStopRequested)
            {
                _log.LogInformation("Stop requested during monitor loop - {Name}", server.Name);
                await GuardConfigAfterCloseAsync().ConfigureAwait(false);
                return new MonitorResult(MonitorOutcome.UserStopped, null);
            }
            if (!_process.IsGameRunning(_currentGame))
            {
                _log.LogInformation("HLL is not running, stopping seeding - {Name}", server.Name);
                _state.RequestStop();
                Emit(new SeedingEvent.HllClosed());
                _keepAwake.Release();
                await GuardConfigAfterCloseAsync().ConfigureAwait(false);
                return new MonitorResult(MonitorOutcome.GameClosed, null);
            }

            var d = await TryGetDirectiveAsync(index, sessionId, ct).ConfigureAwait(false);
            if (d is null)
            {
                // API unreachable — keep seeding and retry at the (config-driven) fallback cadence.
                await InterruptibleSleepAsync((int)_configProvider.Current.PollFallbackSecs, ct).ConfigureAwait(false);
                continue;
            }

            if (d.Action is DirectiveAction.Stay or DirectiveAction.Seed)
            {
                await InterruptibleSleepAsync(d.PollAgainInSecs, ct).ConfigureAwait(false);
                continue;
            }

            // Switch or Stop → we must leave this server. Stagger first (server-computed) for a Switch.
            if (d.Action == DirectiveAction.Switch && d.StaggerSecs > 0)
            {
                _log.LogInformation("Switch needed, staggering ~{Secs}s - {Name}", d.StaggerSecs, server.Name);
                await InterruptibleSleepAsync(d.StaggerSecs, ct).ConfigureAwait(false);
                if (_state.IsStopRequested)
                {
                    return new MonitorResult(MonitorOutcome.UserStopped, null);
                }
                // Re-confirm after the stagger — conditions may have cleared.
                var again = await TryGetDirectiveAsync(index, sessionId, ct).ConfigureAwait(false) ?? d;
                if (again.Action is DirectiveAction.Stay or DirectiveAction.Seed)
                {
                    _log.LogInformation("Switch conditions cleared after stagger, resuming - {Name}", server.Name);
                    continue;
                }
                d = again;
            }

            // Run the countdown (unless the user disabled the switch notification, or it's a hard Stop).
            var notify = _config.GetBool("switch_notification", true);
            if (notify && d.Action == DirectiveAction.Switch)
            {
                var resume = await RunSwitchCountdownAsync(server, index, sessionId, d, ct).ConfigureAwait(false);
                if (_state.IsStopRequested)
                {
                    return new MonitorResult(MonitorOutcome.UserStopped, null);
                }
                if (resume)
                {
                    _state.ClearSwitchState();
                    continue;
                }
            }

            // Execute: kill the game and hand the final directive back to the caller.
            Emit(new SeedingEvent.ServerSwitchExecuting());
            _log.LogInformation("Executing {Action}, killing HLL - {Name}", d.Action, server.Name);
            KillGameProcess();
            _state.ClearSwitchState();
            await Task.Delay(TimeSpan.FromSeconds(PostKillWaitSecs), ct).ConfigureAwait(false);
            await GuardConfigAfterCloseAsync().ConfigureAwait(false);
            return new MonitorResult(MonitorOutcome.Directed, d);
        }
    }

    /// <summary>Run the switch countdown (duration + snooze bounds from the directive) with snooze and
    /// switch-now handling. Returns true if monitoring should resume (a re-poll after a snooze shows the
    /// switch is no longer needed).</summary>
    private async Task<bool> RunSwitchCountdownAsync(
        ServerInfo server, int index, string? sessionId, SeedingDirective directive, CancellationToken ct)
    {
        _log.LogInformation("Starting server switch countdown - {Name}", server.Name);
        _state.ClearSwitchState();
        _window.FocusSeedingWindow();

        var target = directive.Target?.Server.ShortName ?? server.ShortName;
        Emit(new SeedingEvent.ServerSwitchPending(directive.CountdownSecs, target, "candidate_changed"));

        for (var i = 0; i < directive.CountdownSecs; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

            if (_state.IsStopRequested)
            {
                Emit(new SeedingEvent.ServerSwitchCancelled());
                return false;
            }
            if (_state.IsSwitchNowRequested)
            {
                _state.ClearSwitchState();
                return false;
            }
            if (_state.IsSwitchSnoozed)
            {
                var snoozeSecs = _state.TakeSnoozeDuration();
                _log.LogInformation("Server switch snoozed for {Secs}s - {Name}", snoozeSecs, server.Name);
                Emit(new SeedingEvent.ServerSwitchSnoozed(snoozeSecs));

                for (var s = 0; s < snoozeSecs; s++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    if (_state.IsStopRequested)
                    {
                        Emit(new SeedingEvent.ServerSwitchCancelled());
                        return false;
                    }
                    if (_state.IsSwitchNowRequested)
                    {
                        _state.ClearSwitchState();
                        return false;
                    }
                }

                // Snooze expired: re-poll the directive — if it no longer wants a switch, resume.
                var again = await TryGetDirectiveAsync(index, sessionId, ct).ConfigureAwait(false);
                if (again is not null && again.Action is DirectiveAction.Stay or DirectiveAction.Seed)
                {
                    _log.LogInformation("Switch no longer needed after snooze, resuming - {Name}", server.Name);
                    Emit(new SeedingEvent.ServerSwitchCancelled());
                    return true;
                }
                return false;
            }
        }

        return false;
    }

    // ── Launch watcher (port of spawn_launch_watcher / cancel_launch_watcher) ───

    private void CancelLaunchWatcher() => Volatile.Write(ref _launchWatcherActive, 0);

    /// <summary>Spawn a background watcher that, after a failed seeding start, kills any phantom HLL
    /// launch (e.g. Steam was mid-update), retries seeding, and runs the monitor loop. Only one
    /// watcher runs at a time. Port of <c>spawn_launch_watcher</c>.</summary>
    private void SpawnLaunchWatcher(ServerInfo server, int serverIndex)
    {
        if (Interlocked.CompareExchange(ref _launchWatcherActive, 1, 0) != 0)
        {
            return; // a watcher is already running
        }

        var ct = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (Volatile.Read(ref _launchWatcherActive) == 0)
                {
                    _log.LogInformation("Launch watcher cancelled before starting");
                    return;
                }

                Emit(new SeedingEvent.SeedingUpdateWaiting(serverIndex));

                var start = Stopwatch.StartNew();
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    if (Volatile.Read(ref _launchWatcherActive) == 0)
                    {
                        _log.LogInformation("Launch watcher cancelled");
                        return;
                    }
                    if (start.Elapsed.TotalSeconds >= LaunchWatcherTimeoutSecs)
                    {
                        _log.LogInformation("Launch watcher timed out after {Secs} seconds", LaunchWatcherTimeoutSecs);
                        Volatile.Write(ref _launchWatcherActive, 0);
                        Emit(new SeedingEvent.SeedingUpdateTimeout());
                        return;
                    }
                    if (!_process.IsGameRunning(_currentGame))
                    {
                        continue;
                    }

                    _log.LogWarning("Launch watcher detected phantom HLL launch after failed seeding start, killing");
                    KillGameProcess();
                    await Task.Delay(TimeSpan.FromSeconds(PostKillWaitSecs), ct).ConfigureAwait(false);

                    if (Volatile.Read(ref _launchWatcherActive) == 0)
                    {
                        _log.LogInformation("Launch watcher cancelled after killing phantom");
                        return;
                    }
                    if (_state.IsStopRequested)
                    {
                        _log.LogInformation("Stop requested, launch watcher exiting");
                        Volatile.Write(ref _launchWatcherActive, 0);
                        return;
                    }

                    _log.LogInformation("Launch watcher retrying seeding for {Name}", server.ShortName);
                    try
                    {
                        await StartSeedingInnerAsync(server, ct).ConfigureAwait(false);

                        if (_state.IsStopRequested)
                        {
                            _log.LogInformation("Stop requested during restart, exiting watcher");
                            await GuardConfigAfterCloseAsync().ConfigureAwait(false);
                            _backup.RestoreAfterSeeding();
                            Volatile.Write(ref _launchWatcherActive, 0);
                            return;
                        }

                        _log.LogInformation("Launch watcher successfully restarted seeding for {Name}", server.ShortName);
                        Volatile.Write(ref _launchWatcherActive, 0);
                        Emit(new SeedingEvent.SeedingUpdateStarted(serverIndex));

                        await MonitorLoopAsync(server, serverIndex, null, ct).ConfigureAwait(false);
                        _backup.RestoreAfterSeeding();
                        return;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _log.LogInformation(e, "Launch watcher restart failed, continuing to watch");
                        _backup.RestoreAfterSeeding();
                        // Keep watching — Steam may have more buffered commands.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _launchWatcherActive, 0);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Launch watcher crashed");
                Volatile.Write(ref _launchWatcherActive, 0);
            }
        }, ct);
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { /* already disposed */ }
        _lifetime.Dispose();
        _monitorSignal.Dispose();
        _keepAwake.Release();
    }
}
