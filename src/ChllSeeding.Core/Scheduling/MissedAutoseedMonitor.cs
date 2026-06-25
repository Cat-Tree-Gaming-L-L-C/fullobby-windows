using ChllSeeding.Core.Api;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Games;
using ChllSeeding.Core.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Scheduling;

/// <summary>
/// Background watchdog that fires the auto-seed when Task Scheduler missed it — typically after the
/// machine woke from Modern Standby past the scheduled time. Polls every 60s; if the task is
/// installed, its stored UTC time has passed within the last (config-driven) missed window, and it
/// hasn't already fired today (and nothing is in progress / HLL isn't running), it raises
/// <see cref="AutoseedDue"/>. Port of <c>check_missed_autoseed</c> + the 60s poll in <c>app.rs</c>,
/// collapsed to one slot.
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

    /// <summary>Raised (off the UI thread) when the missed seed should run.</summary>
    public event Action? AutoseedDue;

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
        var slot = AutoSeedSlot.Default;
        if (_state.IsInProgress || _state.WasTriggeredToday())
        {
            return;
        }
        if (!await _tasks.IsInstalledAsync(slot.TaskName, ct).ConfigureAwait(false))
        {
            return;
        }
        if (!AutoSeedTime.TryParseStoredUtc(_config.GetString(slot.StoreKey), out var scheduledUtc))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var windowHours = _configProvider.Current.MissedAutoseedWindowHours;
        if (!IsWithinMissedWindow(nowUtc, scheduledUtc, windowHours))
        {
            return;
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

        _state.RecordTriggered();
        AutoseedDue?.Invoke();
    }

    /// <summary>
    /// True when <paramref name="nowUtc"/> is at or after today's <paramref name="scheduledUtc"/> and
    /// strictly less than <paramref name="windowHours"/> past it — i.e. the window is [0, windowHours).
    /// The upper bound is exclusive to match Rust's <c>diff.num_hours() &gt;= MISSED_AUTOSEED_WINDOW_HOURS</c>
    /// skip (autoseed.rs:335), which drops anything at or beyond the whole-hour mark. Pure; unit-tested.
    /// </summary>
    public static bool IsWithinMissedWindow(DateTime nowUtc, TimeOnly scheduledUtc, int windowHours)
    {
        var scheduled = new DateTime(DateOnly.FromDateTime(nowUtc), scheduledUtc, DateTimeKind.Utc);
        var delta = nowUtc - scheduled;
        return delta >= TimeSpan.Zero && delta < TimeSpan.FromHours(windowHours);
    }

    public void Dispose() => _cts?.Dispose();
}
