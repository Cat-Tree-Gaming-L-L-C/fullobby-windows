namespace Fullobby.Core.Scheduling;

/// <summary>
/// Shared coordination state for the auto-seed flow, held as a DI-singleton instance (in-progress
/// flag, cancelled flag, and per-wake last-trigger dates).
/// Guards against running two auto-seeds at once and firing the same wake's missed-seed twice in
/// one (UTC) day, and carries the countdown-cancel flag. "Triggered today" is keyed per wake
/// ("HH:MM" UTC): a machine with 06:00 and 22:00 wakes must still get its evening seed after the
/// morning one ran. The in-progress flag stays machine-wide — only one seed runs at a time.
/// </summary>
public sealed class AutoSeedState
{
    /// <summary>Key for a launch whose wake is unknown (a legacy "--autoseed" task, which exists
    /// only while the machine has a single wake).</summary>
    public const string UnknownWakeKey = "";

    private readonly object _gate = new();
    private readonly Dictionary<string, DateOnly> _triggeredDates = new(StringComparer.Ordinal);

    private int _inProgress; // 0 = idle, 1 = an auto-seed is running
    private volatile bool _cancelled;

    /// <summary>True while an auto-seed (countdown or seeding) is running.</summary>
    public bool IsInProgress => Volatile.Read(ref _inProgress) == 1;

    /// <summary>Atomically claim the auto-seed slot. Returns false if one is already in progress.</summary>
    public bool TryBegin()
    {
        if (Interlocked.CompareExchange(ref _inProgress, 1, 0) != 0)
        {
            return false;
        }
        _cancelled = false;
        return true;
    }

    /// <summary>Release the auto-seed slot (idempotent).</summary>
    public void End() => Volatile.Write(ref _inProgress, 0);

    /// <summary>Request cancellation of the current countdown.</summary>
    public void Cancel()
    {
        _cancelled = true;
        End();
    }

    /// <summary>Whether the countdown has been cancelled.</summary>
    public bool IsCancelled => _cancelled;

    /// <summary>Record that the given wake was triggered today (UTC).</summary>
    public void RecordTriggered(string wakeKey = UnknownWakeKey) =>
        RecordTriggered(wakeKey, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Testable overload taking the current UTC date.</summary>
    public void RecordTriggered(string wakeKey, DateOnly utcToday)
    {
        lock (_gate) { _triggeredDates[wakeKey] = utcToday; }
    }

    /// <summary>Whether the given wake was already triggered today (UTC).</summary>
    public bool WasTriggeredToday(string wakeKey = UnknownWakeKey) =>
        WasTriggeredToday(wakeKey, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Testable overload taking the current UTC date.</summary>
    public bool WasTriggeredToday(string wakeKey, DateOnly utcToday)
    {
        lock (_gate) { return _triggeredDates.TryGetValue(wakeKey, out var d) && d == utcToday; }
    }
}
