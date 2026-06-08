using System.Collections.ObjectModel;
using ChllSeeder.Core.Api;
using ChllSeeder.Core.Bootstrap;
using ChllSeeder.Core.Config;
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
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer;

    /// <summary>Set by the hosting page (which has a XamlRoot) so the VM can ask the user to
    /// confirm closing a running game. Returns true when the user confirms.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public SeedingViewModel(
        ILogger<SeedingViewModel> log,
        SeedingEngine engine,
        SeedingApiClient api,
        ServerStore servers,
        ConfigService config,
        AppBootstrapper bootstrap)
    {
        _log = log;
        _engine = engine;
        _api = api;
        _servers = servers;
        _config = config;
        _bootstrap = bootstrap;

        // Constructed on the UI thread (first page resolve), so this captures the UI queue.
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        euEnabled = _config.GetBool("eu_enabled");
        efficiencyMode = _config.GetBool("efficiency_mode");

        _engine.Event += OnEngineEvent;
        _bootstrap.ServersLoaded += OnServersLoaded;
        _bootstrap.ServersLoadFailed += OnServersLoadFailed;
        _bootstrap.StatsUpdated += OnStatsUpdated;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // If the bootstrapper already loaded servers before this VM existed, reflect that now.
        if (_bootstrap.HasServers)
        {
            RebuildServers();
        }
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

    [ObservableProperty]
    private string errorMessage = "";

    [ObservableProperty]
    private bool showSeedButtons = true;

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

    partial void OnErrorMessageChanged(string value) => RefreshDerived();

    /// <summary>Recompute the banner text and which button group is visible. Mirrors the
    /// show_* derivations in seed.rs and the status_config map in seed_banner.rs.</summary>
    private void RefreshDerived()
    {
        ShowSeedButtons = Status is SeedingStatus.Idle or SeedingStatus.Stopped or SeedingStatus.Error
            || (Status == SeedingStatus.Running && !IsSeeding);

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
            SeedingStatus.Error => ErrorMessage.Length > 0 ? ErrorMessage : "An error occurred. Please try again.",
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

        ErrorMessage = "";
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
            ErrorMessage = "Couldn't reach the seeding service. Please try again.";
            IsSeeding = false;
            SetStatus(SeedingStatus.Error);
            return;
        }

        var candidate = region == "eu" ? status.Hll.Eu : status.Hll.Na;
        if (candidate is null)
        {
            ErrorMessage = region == "eu"
                ? "All EU servers are full — no seeding needed."
                : "All NA servers are full — no seeding needed.";
            IsSeeding = false;
            SetStatus(SeedingStatus.Error);
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

    /// <summary>Launch + monitor a specific candidate index. Resolves the terminal status once
    /// the monitor loop returns (or hands off to the launch watcher on a "could not open").</summary>
    private async Task RunSeedAsync(int index, string region)
    {
        SetStatus(SeedingStatus.Initializing);
        try
        {
            var actual = await _engine.StartSeedingAsync(index, region).ConfigureAwait(true);
            SetStatus(SeedingStatus.Seeding);
            await _engine.MonitorSeedAsync(actual, region).ConfigureAwait(true);
        }
        catch (SeedingException e) when (e.Message.Contains("could not open", StringComparison.OrdinalIgnoreCase))
        {
            // The engine restored settings and spawned the launch watcher; its events
            // (SeedingUpdate*) drive the rest of the flow from here.
            SetStatus(SeedingStatus.WaitingForUpdate);
            return;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Seeding failed");
            ErrorMessage = "Failed to launch the game. Please try again.";
            IsSeeding = false;
            SetStatus(SeedingStatus.Error);
            return;
        }

        // Monitor returned: HLL closed, switched away, or a stop was requested.
        if (Status is not (SeedingStatus.Error or SeedingStatus.WaitingForUpdate))
        {
            SetStatus(Status == SeedingStatus.Stopping ? SeedingStatus.Stopped : SeedingStatus.Idle);
        }
        IsSeeding = false;
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

        ErrorMessage = "";
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
            ErrorMessage = "Failed to launch the game. Please try again.";
            SetStatus(SeedingStatus.Error);
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
        try
        {
            await _engine.StopSeedingAsync().ConfigureAwait(true);
            SetStatus(SeedingStatus.Stopped);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error stopping seeding");
            ErrorMessage = "Failed to stop seeding cleanly.";
            SetStatus(SeedingStatus.Error);
        }
        IsSeeding = false;
    }

    /// <summary>Stop the seeding monitor but leave the game running. Port of do_stop_seed_only.</summary>
    [RelayCommand]
    private async Task StopSeedingOnlyAsync()
    {
        SetStatus(SeedingStatus.Stopping);
        ResetSwitchOverlay();
        try
        {
            await _engine.StopSeedingOnlyAsync().ConfigureAwait(true);
            SetStatus(SeedingStatus.Stopped);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error stopping seeding (keep game)");
            ErrorMessage = "Failed to stop seeding cleanly.";
            SetStatus(SeedingStatus.Error);
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
                break;
            case SeedingEvent.ServerSwitchSnoozed sn:
                ServerSwitchSnoozed = true;
                _switchSnoozeRemaining = sn.SnoozeSecs;
                UpdateSwitchText();
                break;
            case SeedingEvent.ServerSwitchExecuting:
                ResetSwitchOverlay();
                SetStatus(SeedingStatus.Switching);
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
                ErrorMessage = "The game didn't finish updating in time. Please try again.";
                IsSeeding = false;
                SetStatus(SeedingStatus.Error);
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
