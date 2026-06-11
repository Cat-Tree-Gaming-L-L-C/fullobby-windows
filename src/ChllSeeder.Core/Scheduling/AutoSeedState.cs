namespace ChllSeeder.Core.Scheduling;

/// <summary>
/// Shared coordination state for the auto-seed flow, replacing the Rust process-global atomics
/// (<c>AUTOSEED_IN_PROGRESS</c>, <c>AUTOSEED_CANCELLED</c>, <c>LAST_AUTOSEED_TRIGGER</c>) with a
/// DI-singleton instance. Guards against running two auto-seeds at once and firing the same region's
/// missed-seed twice in one (UTC) day, and carries the countdown-cancel flag.
/// </summary>
public sealed class AutoSeedState
{
    private readonly object _gate = new();
    private readonly HashSet<(DateOnly Date, string Region)> _triggered = [];

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

    /// <summary>Record that <paramref name="region"/> was triggered today (UTC), pruning stale days.</summary>
    public void RecordTriggered(string region) => RecordTriggered(region, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Testable overload taking the current UTC date.</summary>
    public void RecordTriggered(string region, DateOnly utcToday)
    {
        lock (_gate)
        {
            _triggered.RemoveWhere(e => e.Date != utcToday);
            _triggered.Add((utcToday, region));
        }
    }

    /// <summary>Whether <paramref name="region"/> has already been triggered today (UTC).</summary>
    public bool WasTriggeredToday(string region) =>
        WasTriggeredToday(region, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Testable overload taking the current UTC date.</summary>
    public bool WasTriggeredToday(string region, DateOnly utcToday)
    {
        lock (_gate)
        {
            return _triggered.Contains((utcToday, region));
        }
    }
}
