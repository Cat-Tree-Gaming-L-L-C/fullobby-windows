using System.Diagnostics;
using ChllSeeder.Core.Api;
using ChllSeeder.Core.Config;
using ChllSeeder.Core.Games;
using ChllSeeder.Core.Native;
using ChllSeeder.Core.Servers;
using ChllSeeder.Core.Tools;
using Microsoft.Extensions.Logging;
using Windows.Win32;

namespace ChllSeeder.Core.Seeding;

/// <summary>
/// Drives a full seeding session: launch the game through Steam, bypass the splash/EAC,
/// then monitor server population and rotate/stop as needed. Port of the core state machine
/// in <c>src-rust/src/backend/seeding.rs</c> (process-global atomics/tokio-tasks →
/// DI-singleton instance state + Task-based background work). Runs entirely off the UI thread;
/// the UI subscribes to <see cref="Event"/> for countdowns, banners, and switch prompts.
/// </summary>
public sealed class SeedingEngine : IDisposable
{
    // ── Timing constants (seconds) — verbatim from seeding.rs ──────────────────
    public const long SplashBypassMinSecs = 10;
    public const long SplashBypassMaxSecs = 60;
    public const long SplashBypassDefaultSecs = 20;

    public const long EacLaunchTimeoutSecs = 180;
    public const long WindowWaitTimeoutSecs = 60;
    public const long GameOpenFirstRetrySecs = 60;
    public const long GameOpenSecondRetrySecs = 120;
    public const long GameOpenTimeoutSecs = 180;

    public const long MaxSeedingDurationSecs = 60 * 60 * 5; // 5 hours
    public const long PostKillWaitSecs = 20;
    public const long ServerSwitchCountdownSecs = 30;

    public const long LaunchWatcherTimeoutSecs = 600; // 10 min

    public const long MonitorMinIntervalSecs = 15;
    public const long MonitorMaxIntervalSecs = 60;
    public const long MonitorBackoffStepSecs = 15;

    public const long StaggerMaxSecs = 90;
    public const long StaggerJitterMaxSecs = 15;

    public const long ForcedRefreshIntervalSecs = 180;
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

    /// <summary>Start seeding the NA/EU server at <paramref name="serverNumber"/>: launch the game
    /// and run the splash bypass. Returns the server index. On launch failure, restores settings,
    /// spawns the launch watcher, and rethrows. The caller then runs <see cref="MonitorSeedAsync"/>.</summary>
    public Task<int> StartSeedingAsync(int serverNumber, string region = "na", CancellationToken ct = default) =>
        StartSeedingImplAsync(serverNumber, region, ct);

    /// <summary>Direct launch + focus (no seeding monitor, no efficiency mode). Port of <c>start_impl</c>.</summary>
    public Task StartAsync(int serverNumber, string region = "na", CancellationToken ct = default) =>
        StartImplAsync(serverNumber, region, ct);

    /// <summary>Run the population monitor/rotation loop for an already-launched seed. Port of
    /// <c>monitor_seed_impl</c>.</summary>
    public Task MonitorSeedAsync(int serverNumber, string region = "na", CancellationToken ct = default) =>
        MonitorSeedImplAsync(serverNumber, region, ct);

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

    /// <summary>Snooze a pending server switch for the given duration (clamped to 60–1800s).</summary>
    public void SnoozeServerSwitch(long durationSecs)
    {
        var clamped = Math.Clamp(durationSecs, SeedingState.SnoozeMinSecs, SeedingState.SnoozeMaxSecs);
        _log.LogInformation("Server switch snoozed for {Secs}s by user", clamped);
        _state.SnoozeServerSwitch(durationSecs);
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

    // ── Candidate-change check (port of has_candidate_changed) ─────────────────

    private async Task<bool> HasCandidateChangedAsync(
        string gameId, string region, int index, bool forceHttp, CancellationToken ct)
    {
        SeedingStatusResponse? status;
        if (forceHttp)
        {
            try
            {
                status = await _api.GetSeedingStatusAsync(ct).ConfigureAwait(false);
                _statusCache.Update(status);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Forced refresh failed, falling back to cache");
                status = _statusCache.GetCached();
                if (status is null)
                {
                    _log.LogWarning("No cached seeding status available either");
                    return false;
                }
            }
        }
        else
        {
            status = _statusCache.GetCached();
            if (status is null)
            {
                try
                {
                    status = await _api.GetSeedingStatusAsync(ct).ConfigureAwait(false);
                    _statusCache.Update(status);
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "Failed to fetch seeding status, continuing monitoring");
                    return false;
                }
            }
        }

        var gameStatus = gameId switch
        {
            "hll" => status.Hll,
            "hllv" => status.Hllv,
            _ => null,
        };
        var candidate = region == "eu" ? gameStatus?.Eu : gameStatus?.Na;

        if (candidate is null)
        {
            _log.LogInformation("No candidate for {Game}:{Region}, triggering switch", gameId, region);
            return true;
        }
        if (candidate.Index != index)
        {
            _log.LogInformation("Candidate changed from {Old} to {New} in {Game}:{Region}, triggering switch",
                index, candidate.Index, gameId, region);
            return true;
        }
        return false;
    }

    // ── Stagger (port of compute_stagger_secs) ─────────────────────────────────

    /// <summary>Compute the fill-based server-switch stagger delay: full server → 0s, barely above
    /// the seeding threshold → <see cref="StaggerMaxSecs"/>, no population data → half of max.
    /// Pure and dependency-free for unit coverage. Port of <c>compute_stagger_secs</c>.</summary>
    public static long ComputeStaggerSecs(int threshold, (int Players, int MaxPlayers)? counts)
    {
        if (counts is not { } c)
        {
            return StaggerMaxSecs / 2; // no data yet — mid-range default
        }

        double headroom = Math.Max(c.MaxPlayers - threshold, 1);
        double aboveThreshold = Math.Max(c.Players - threshold, 0);
        var fillRatio = Math.Clamp(aboveThreshold / headroom, 0.0, 1.0);

        return (long)(StaggerMaxSecs * (1.0 - fillRatio)); // truncating cast, matching Rust `as u64`
    }

    private long StaggerForServer(ServerInfo server) =>
        ComputeStaggerSecs(server.SeedingThreshold, _servers.GetPlayerCount(server.Name));

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
        var bypassDuration = SplashBypassDefaultSecs;
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

    private async Task<int> StartSeedingImplAsync(int serverNumber, string region, CancellationToken ct)
    {
        ClearStopForNewSession();
        CancelLaunchWatcher();
        _keepAwake.Acquire();

        if (_process.IsGameRunning(_currentGame))
        {
            _log.LogInformation("HLL is already running, exiting start_seeding ({Region})", region);
            return serverNumber;
        }

        var server = _servers.GetServerByRegion(region, serverNumber)
            ?? throw new SeedingException($"No server at {region}:{serverNumber}");

        try
        {
            await StartSeedingInnerAsync(server, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _backup.RestoreAfterSeeding();
            SpawnLaunchWatcher(server, serverNumber, region);
            throw;
        }

        _log.LogInformation("Seeding started ({Region} region). Monitor process will now run. - {Name}",
            region.ToUpperInvariant(), server.Name);
        return serverNumber;
    }

    private async Task StartImplAsync(int serverNumber, string region, CancellationToken ct)
    {
        CancelLaunchWatcher();
        var server = _servers.GetServerByRegion(region, serverNumber)
            ?? throw new SeedingException($"No server at {region}:{serverNumber}");

        // Direct launch: do NOT apply efficiency mode.
        await OpenGameAsync(server, applyEfficiency: false, ct).ConfigureAwait(false);
        await FocusHllAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Start {Region} complete.", region.ToUpperInvariant());
    }

    private async Task MonitorSeedImplAsync(int serverNumber, string region, CancellationToken ct)
    {
        _log.LogInformation("Monitoring {Region} seed running", region.ToUpperInvariant());
        var elapsed = Stopwatch.StartNew();

        var server = _servers.GetServerByRegion(region, serverNumber)
            ?? throw new SeedingException($"No server at {region}:{serverNumber}");

        try
        {
            await MonitorLoopAsync(elapsed, server, region, serverNumber, ct).ConfigureAwait(false);
        }
        finally
        {
            // Mirror the Rust do_monitor_seed cleanup: restore the user's real settings (this also
            // resets the efficiency-applied flag so it can re-apply on the next seed) and drop the
            // keep-awake hold. Both are idempotent, so any monitor-exit path lands clean.
            _backup.RestoreAfterSeeding();
            _keepAwake.Release();
        }
    }

    // ── Monitor loop (port of monitor_loop) ────────────────────────────────────

    private async Task MonitorLoopAsync(
        Stopwatch elapsedTime, ServerInfo server, string region, int index, CancellationToken ct)
    {
        // Entry check: if the candidate already changed before we start, kill and exit.
        if (await HasCandidateChangedAsync(_currentGame.Id, region, index, false, ct).ConfigureAwait(false))
        {
            _log.LogInformation("Candidate already changed at monitor start, exiting - {Name}", server.Name);
            KillGameProcess();
            await Task.Delay(TimeSpan.FromSeconds(PostKillWaitSecs), ct).ConfigureAwait(false);
            return;
        }

        var currentInterval = MonitorMinIntervalSecs;
        var lastCandidateChanged = false;

        Stopwatch? switchFirstDetected = null;
        var staggerJitter = Random.Shared.NextInt64(0, StaggerJitterMaxSecs + 1);

        var lastForcedRefresh = Stopwatch.StartNew();
        var lastStatusLog = Stopwatch.StartNew();

        while (true)
        {
            if (_state.IsStopRequested)
            {
                _log.LogInformation("Stop requested during monitor loop - {Name}", server.Name);
                break;
            }
            if (!_process.IsGameRunning(_currentGame))
            {
                _log.LogInformation("HLL is not running, stopping seeding - {Name}", server.Name);
                // Set stop so the UI won't try to relaunch after a user-closed game.
                _state.RequestStop();
                Emit(new SeedingEvent.HllClosed());
                _keepAwake.Release();
                break;
            }

            var forceHttp = lastForcedRefresh.Elapsed.TotalSeconds >= ForcedRefreshIntervalSecs;
            if (forceHttp)
            {
                lastForcedRefresh.Restart();
            }

            var candidateChanged = await HasCandidateChangedAsync(_currentGame.Id, region, index, forceHttp, ct)
                .ConfigureAwait(false);

            if (candidateChanged != lastCandidateChanged)
            {
                currentInterval = MonitorMinIntervalSecs;
                lastCandidateChanged = candidateChanged;
            }
            else
            {
                currentInterval = Math.Min(currentInterval + MonitorBackoffStepSecs, MonitorMaxIntervalSecs);
            }

            var isTimedOut = elapsedTime.Elapsed.TotalSeconds > MaxSeedingDurationSecs;
            var shouldSwitch = candidateChanged || isTimedOut;

            if (shouldSwitch && switchFirstDetected is null)
            {
                switchFirstDetected = Stopwatch.StartNew();
                var stagger = StaggerForServer(server);
                var reason = candidateChanged ? "candidate changed" : "time limit reached";
                _log.LogInformation("Server switch needed ({Reason}), fill-based stagger ~{Stagger}s + {Jitter}s jitter - {Name}",
                    reason, stagger, staggerJitter, server.Name);
            }
            else if (!shouldSwitch && switchFirstDetected is not null)
            {
                _log.LogInformation("Switch conditions cleared, cancelling stagger - {Name}", server.Name);
                switchFirstDetected = null;
            }

            var staggerReady = switchFirstDetected is { } sw
                && sw.Elapsed.TotalSeconds >= StaggerForServer(server) + staggerJitter;

            if (shouldSwitch && staggerReady)
            {
                if (_state.IsStopRequested) break;

                if (!_config.GetBool("switch_notification"))
                {
                    _log.LogInformation("Switch notification disabled, killing HLL immediately - {Name}", server.Name);
                    KillGameProcess();
                    _log.LogInformation("Waiting {Secs} seconds for HLL to close - {Name}", PostKillWaitSecs, server.Name);
                    await Task.Delay(TimeSpan.FromSeconds(PostKillWaitSecs), ct).ConfigureAwait(false);
                    break;
                }

                var resumeMonitoring = await RunSwitchCountdownAsync(server, region, index, elapsedTime, candidateChanged, ct)
                    .ConfigureAwait(false);

                if (_state.IsStopRequested) break;

                if (resumeMonitoring)
                {
                    _state.ClearSwitchState();
                    switchFirstDetected = null;
                    continue;
                }

                // Proceed with kill.
                Emit(new SeedingEvent.ServerSwitchExecuting());
                _log.LogInformation("Executing server switch, killing HLL - {Name}", server.Name);
                KillGameProcess();
                _state.ClearSwitchState();
                _log.LogInformation("Waiting {Secs} seconds for HLL to close - {Name}", PostKillWaitSecs, server.Name);
                await Task.Delay(TimeSpan.FromSeconds(PostKillWaitSecs), ct).ConfigureAwait(false);
                break;
            }

            if (lastStatusLog.Elapsed.TotalSeconds >= StatusLogIntervalSecs)
            {
                var fill = _servers.GetPlayerCount(server.Name) is { } pc ? $"{pc.Players}/{pc.MaxPlayers}" : "?";
                _log.LogInformation("Monitor: candidate_changed={Changed}, timed_out={TimedOut}, switch_pending={Pending}, fill={Fill}, elapsed={Mins}m - {Name}",
                    candidateChanged, isTimedOut, switchFirstDetected is not null, fill,
                    (long)elapsedTime.Elapsed.TotalSeconds / 60, server.Name);
                lastStatusLog.Restart();
            }

            // Interruptible sleep: wake early if the monitor is notified (SSE reconnect / stop).
            if (await _monitorSignal.WaitAsync(TimeSpan.FromSeconds(currentInterval), ct).ConfigureAwait(false))
            {
                _log.LogInformation("Monitor woken early (SSE reconnect or stop), re-checking candidate - {Name}", server.Name);
            }
        }

        // After the game exits (natural close or kill), restore config if HLL reset it.
        await GuardConfigAfterCloseAsync().ConfigureAwait(false);
    }

    /// <summary>Run the 30s switch countdown with notification, snooze, and switch-now handling.
    /// Returns true if monitoring should resume (conditions cleared after a snooze). Port of the
    /// <c>'countdown</c> block in <c>monitor_loop</c>.</summary>
    private async Task<bool> RunSwitchCountdownAsync(
        ServerInfo server, string region, int index, Stopwatch elapsedTime, bool candidateChanged, CancellationToken ct)
    {
        _log.LogInformation("Starting server switch countdown - {Name}", server.Name);
        _state.ClearSwitchState();

        // Bring the seeder window forward. The OS toast + switch sound are Phase 2 (the UI
        // subscribes to ServerSwitchPending); the event below carries everything it needs.
        _window.FocusSeederWindow();

        var reason = candidateChanged ? "candidate_changed" : "time_limit";
        Emit(new SeedingEvent.ServerSwitchPending(ServerSwitchCountdownSecs, server.ShortName, reason));

        for (var i = 0; i < ServerSwitchCountdownSecs; i++)
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

                // Snooze expired: re-check with a forced HTTP fetch for the freshest data.
                var stillChanged = await HasCandidateChangedAsync(_currentGame.Id, region, index, true, ct)
                    .ConfigureAwait(false);
                var stillTimedOut = elapsedTime.Elapsed.TotalSeconds > MaxSeedingDurationSecs;

                if (!stillChanged && !stillTimedOut)
                {
                    _log.LogInformation("Conditions changed after snooze, resuming monitoring - {Name}", server.Name);
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
    private void SpawnLaunchWatcher(ServerInfo server, int serverIndex, string region)
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

                Emit(new SeedingEvent.SeedingUpdateWaiting(serverIndex, region));

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
                        Emit(new SeedingEvent.SeedingUpdateStarted(serverIndex, region));

                        var elapsed = Stopwatch.StartNew();
                        await MonitorLoopAsync(elapsed, server, region, serverIndex, ct).ConfigureAwait(false);
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
