using ChllSeeding.Core.Config;
using ChllSeeding.Core.Games;
using ChllSeeding.Core.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Scheduling;

/// <summary>
/// Background watchdog that fires a region's auto-seed when Task Scheduler missed it — typically
/// after the machine woke from Modern Standby past the scheduled time. Polls every 60s; if a region's
/// task is installed, its stored UTC time has passed within the last <see cref="MissedWindowHours"/>
/// hours, and it hasn't already fired today (and nothing is in progress / HLL isn't running), it
/// raises <see cref="AutoseedDue"/>. Port of <c>check_missed_autoseed</c> + the 60s poll in <c>app.rs</c>.
/// </summary>
public sealed class MissedAutoseedMonitor : IHostedService, IDisposable
{
    public const int PollIntervalSecs = 60;
    public const int MissedWindowHours = 4;

    /// <summary>If a poll tick is this much later than expected, the machine likely slept — log it.</summary>
    private const int WakeThresholdSecs = 180;

    private readonly ILogger<MissedAutoseedMonitor> _log;
    private readonly ScheduledTaskService _tasks;
    private readonly ConfigService _config;
    private readonly AutoSeedState _state;
    private readonly ProcessMonitor _process;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Raised (off the UI thread) with the region ("na"/"eu") whose missed seed should run.</summary>
    public event Action<string>? AutoseedDue;

    public MissedAutoseedMonitor(
        ILogger<MissedAutoseedMonitor> log,
        ScheduledTaskService tasks,
        ConfigService config,
        AutoSeedState state,
        ProcessMonitor process)
    {
        _log = log;
        _tasks = tasks;
        _config = config;
        _state = state;
        _process = process;
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
        foreach (var slot in AutoSeedSlot.All)
        {
            if (_state.IsInProgress)
            {
                return; // an auto-seed is already running — nothing else fires this tick
            }
            if (_state.WasTriggeredToday(slot.Region))
            {
                continue;
            }
            if (!await _tasks.IsInstalledAsync(slot.TaskName, ct).ConfigureAwait(false))
            {
                continue;
            }
            if (!AutoSeedTime.TryParseStoredUtc(_config.GetString(slot.StoreKey), out var scheduledUtc))
            {
                continue;
            }

            var nowUtc = DateTime.UtcNow;
            if (!IsWithinMissedWindow(nowUtc, scheduledUtc, MissedWindowHours))
            {
                continue;
            }

            // Final guards: don't stomp an in-progress seed or a hand-launched game.
            if (_state.IsInProgress || _process.IsGameRunning(GameCatalog.Hll))
            {
                continue;
            }

            var late = nowUtc - new DateTime(DateOnly.FromDateTime(nowUtc), scheduledUtc, DateTimeKind.Utc);
            _log.Log(afterWake ? LogLevel.Information : LogLevel.Debug,
                "Missed auto-seed due for {Region}: scheduled {Time} UTC, {Late:F1}h late (afterWake={Wake})",
                slot.Region.ToUpperInvariant(), scheduledUtc, late.TotalHours, afterWake);

            _state.RecordTriggered(slot.Region);
            AutoseedDue?.Invoke(slot.Region);
            return; // one per tick; the in-progress guard handles the rest
        }
    }

    /// <summary>
    /// True when <paramref name="nowUtc"/> is at or after today's <paramref name="scheduledUtc"/> and
    /// strictly less than <paramref name="windowHours"/> past it — i.e. the window is [0, windowHours).
    /// Matches the Rust check (<c>diff.num_hours() &gt;= MISSED_AUTOSEED_WINDOW_HOURS</c> excludes the
    /// boundary). Pure; unit-tested.
    /// </summary>
    public static bool IsWithinMissedWindow(DateTime nowUtc, TimeOnly scheduledUtc, int windowHours)
    {
        var scheduled = new DateTime(DateOnly.FromDateTime(nowUtc), scheduledUtc, DateTimeKind.Utc);
        var delta = nowUtc - scheduled;
        return delta >= TimeSpan.Zero && delta < TimeSpan.FromHours(windowHours);
    }

    public void Dispose() => _cts?.Dispose();
}
