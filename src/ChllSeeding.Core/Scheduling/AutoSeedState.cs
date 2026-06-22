namespace ChllSeeding.Core.Scheduling;

/// <summary>
/// Shared coordination state for the auto-seed flow, replacing the Rust process-global atomics
/// (<c>AUTOSEED_IN_PROGRESS</c>, <c>AUTOSEED_CANCELLED</c>, <c>LAST_AUTOSEED_TRIGGER</c>) with a
/// DI-singleton instance. Guards against running two auto-seeds at once and firing the missed-seed
/// twice in one (UTC) day, and carries the countdown-cancel flag. Region removed — a single daily
/// auto-seed means a single "triggered today" flag.
/// </summary>
public sealed class AutoSeedState
{
    private readonly object _gate = new();
    private DateOnly? _triggeredDate;

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

    /// <summary>Record that the auto-seed was triggered today (UTC).</summary>
    public void RecordTriggered() => RecordTriggered(DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Testable overload taking the current UTC date.</summary>
    public void RecordTriggered(DateOnly utcToday)
    {
        lock (_gate) { _triggeredDate = utcToday; }
    }

    /// <summary>Whether the auto-seed has already been triggered today (UTC).</summary>
    public bool WasTriggeredToday() => WasTriggeredToday(DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Testable overload taking the current UTC date.</summary>
    public bool WasTriggeredToday(DateOnly utcToday)
    {
        lock (_gate) { return _triggeredDate == utcToday; }
    }
}
