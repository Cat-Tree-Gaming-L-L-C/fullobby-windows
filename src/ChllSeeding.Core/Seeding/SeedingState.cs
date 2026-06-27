namespace ChllSeeding.Core.Seeding;

/// <summary>
/// Thread-safe coordination flags shared between the seeding command surface and the
/// running monitor loop: the global stop request, the server-switch snooze, and the
/// "switch now" confirmation. Kept dependency-free so the lifecycle logic is unit-testable
/// in isolation (stop / snooze / switch-now).
/// </summary>
public sealed class SeedingState
{
    // Snooze clamp bounds (seconds).
    public const long SnoozeMinSecs = 60;
    public const long SnoozeMaxSecs = 1800;

    private volatile bool _stopRequested;
    private volatile bool _switchNowRequested;

    // 0 = not snoozed, >0 = snoozed for N seconds. Guarded by Interlocked.
    private long _snoozeDurationSecs;

    // ── Stop ────────────────────────────────────────────────────────────────

    public bool IsStopRequested => _stopRequested;

    public void RequestStop() => _stopRequested = true;

    /// <summary>Clear the stop flag for a new seeding session. (The once-per-session config-backup
    /// flag is reset separately: it lives on the engine, which clears it alongside this call.)</summary>
    public void ClearStop() => _stopRequested = false;

    // ── Server-switch snooze ──────────────────────────────────────────────────

    public bool IsSwitchSnoozed => Interlocked.Read(ref _snoozeDurationSecs) > 0;

    /// <summary>Store a snooze duration, flooring at 1s so a 0 still registers as snoozed
    /// (matches <c>set_switch_snooze</c>'s <c>.max(1)</c>).</summary>
    public void SetSwitchSnooze(long durationSecs) =>
        Interlocked.Exchange(ref _snoozeDurationSecs, Math.Max(durationSecs, 1));

    /// <summary>Atomically read and clear the snooze duration (0 if not snoozed).</summary>
    public long TakeSnoozeDuration() => Interlocked.Exchange(ref _snoozeDurationSecs, 0);

    /// <summary>User-facing snooze: clamp to [60, 1800]s, then store. Port of
    /// <c>snooze_server_switch</c>.</summary>
    public void SnoozeServerSwitch(long durationSecs) =>
        SetSwitchSnooze(Math.Clamp(durationSecs, SnoozeMinSecs, SnoozeMaxSecs));

    // ── "Switch now" confirmation ─────────────────────────────────────────────

    public bool IsSwitchNowRequested => _switchNowRequested;

    public void RequestSwitchNow() => _switchNowRequested = true;

    /// <summary>Reset both switch-coordination flags (snooze + switch-now).</summary>
    public void ClearSwitchState()
    {
        Interlocked.Exchange(ref _snoozeDurationSecs, 0);
        _switchNowRequested = false;
    }
}
