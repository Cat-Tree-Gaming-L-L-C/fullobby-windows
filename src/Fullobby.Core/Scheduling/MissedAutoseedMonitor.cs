using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Fullobby.Core.Games;
using Fullobby.Core.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// Background watchdog that fires an auto-seed when Task Scheduler missed one — typically after the
/// machine woke from Modern Standby past a scheduled time. Polls every 60s over the stored wake
/// set; if a wake's task is installed, its UTC time has passed within that wake's missed window,
/// and that wake hasn't already fired today (and nothing is in progress / HLL isn't running), it
/// raises <see cref="AutoseedDue"/> with the wake's "HH:MM" key. At most one wake fires per tick —
/// only one seed runs at a time anyway.
/// </summary>
public sealed class MissedAutoseedMonitor : IHostedService, IDisposable
{
    public const int PollIntervalSecs = 60;

    /// <summary>If a poll tick is this much later than expected, the machine likely slept — log it.</summary>
    private const int WakeThresholdSecs = 180;

    private readonly ILogger<MissedAutoseedMonitor> _log;
    private readonly ScheduledTaskService _tasks;
    private readonly ConfigService _config;
    private readonly AutoSeedState _state;
    private readonly ProcessMonitor _process;
    private readonly SeedingConfigProvider _configProvider;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Raised (off the UI thread) when a missed seed should run, with the wake's
    /// "HH:MM" UTC key.</summary>
    public event Action<string>? AutoseedDue;

    public MissedAutoseedMonitor(
        ILogger<MissedAutoseedMonitor> log,
        ScheduledTaskService tasks,
        ConfigService config,
        AutoSeedState state,
        ProcessMonitor process,
        SeedingConfigProvider configProvider)
    {
        _log = log;
        _tasks = tasks;
        _config = config;
        _state = state;
        _process = process;
        _configProvider = configProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected */ }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var lastTick = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSecs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = Environment.TickCount64;
            var afterWake = (now - lastTick) > WakeThresholdSecs * 1000L;
            lastTick = now;

            try
            {
                await CheckOnceAsync(afterWake, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Missed-autoseed check failed");
            }
        }
    }

    private async Task CheckOnceAsync(bool afterWake, CancellationToken ct)
    {
        // Order matters: everything above the IsInstalledAsync call is in-memory, while that call
        // spawns schtasks.exe. With the process spawn first, this loop cost ~1,440 process
        // creations per day on every machine — including the majority that never configured
        // auto-seed at all. Confirming installation last (and only for a wake already inside its
        // missed window) keeps it to a few calls a day for a configured user and none for anyone
        // else.
        var onboarded = _config.GetBool(ConfigKeys.OnboardingComplete);
        var nowUtc = DateTime.UtcNow;
        var fleetWindowHours = _configProvider.Current.MissedAutoseedWindowHours;

        foreach (var wake in WakeStore.Load(_config))
        {
            if (!AutoSeedTime.TryParseStoredUtc(wake.TimeUtc, out var scheduledUtc))
            {
                continue;
            }
            if (!IsEligibleToFire(onboarded, _state.IsInProgress, _state.WasTriggeredToday(wake.TimeUtc)))
            {
                continue;
            }
            // The wake's own missed window (max across its justifying networks, resolved at plan
            // time); the unattributed fleet wake follows the live fleet value.
            if (!IsWithinMissedWindow(nowUtc, scheduledUtc, wake.MissedWindowHours ?? fleetWindowHours))
            {
                continue;
            }

            if (!await _tasks.IsInstalledAsync(wake.Slot.TaskName, ct).ConfigureAwait(false))
            {
                continue;
            }

            // Final guards: don't stomp an in-progress seed or a hand-launched game.
            if (_state.IsInProgress || _process.IsGameRunning(GameCatalog.Hll))
            {
                return;
            }

            var late = nowUtc - new DateTime(DateOnly.FromDateTime(nowUtc), scheduledUtc, DateTimeKind.Utc);
            _log.Log(afterWake ? LogLevel.Information : LogLevel.Debug,
                "Missed auto-seed due: scheduled {Time} UTC, {Late:F1}h late (afterWake={Wake})",
                scheduledUtc, late.TotalHours, afterWake);

            _state.RecordTriggered(wake.TimeUtc);
            AutoseedDue?.Invoke(wake.TimeUtc);
            return; // one seed per tick — the next wake gets its turn once this one is done
        }
    }

    /// <summary>
    /// The in-memory pre-checks that must all pass before the (process-spawning) task query.
    ///
    /// The onboarding check is the important one: the scheduled task lives in Windows Task
    /// Scheduler, which outlives our config. A reinstall, a wiped config, or any auth reset that
    /// re-arms the wizard (rejected credentials, unprotectable secrets) leaves the task installed
    /// while the app is back at first run — and without this the watchdog would raise a desktop
    /// notification and launch the game behind the onboarding overlay, for a user who has not yet
    /// agreed to anything. Checked here rather than at the point of firing so nothing is recorded
    /// as "triggered today": if onboarding completes while still inside the missed window, the
    /// seed runs normally on a later tick. Pure; unit-tested.
    /// </summary>
    public static bool IsEligibleToFire(bool onboardingComplete, bool inProgress, bool triggeredToday) =>
        onboardingComplete && !inProgress && !triggeredToday;

    /// <summary>
    /// True when <paramref name="nowUtc"/> is at or after today's <paramref name="scheduledUtc"/> and
    /// strictly less than <paramref name="windowHours"/> past it — i.e. the window is [0, windowHours).
    /// The upper bound is exclusive: a diff of exactly <paramref name="windowHours"/> is treated as
    /// out of window, dropping anything at or beyond the whole-hour mark. Pure; unit-tested.
    /// </summary>
    public static bool IsWithinMissedWindow(DateTime nowUtc, TimeOnly scheduledUtc, int windowHours)
    {
        var scheduled = new DateTime(DateOnly.FromDateTime(nowUtc), scheduledUtc, DateTimeKind.Utc);
        var delta = nowUtc - scheduled;
        return delta >= TimeSpan.Zero && delta < TimeSpan.FromHours(windowHours);
    }

    public void Dispose() => _cts?.Dispose();
}
