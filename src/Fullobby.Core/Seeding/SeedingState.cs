namespace Fullobby.Core.Seeding;

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

    // Restart-and-rejoin coordination (server-driven join-stall recovery). All guarded by _rejoinGate.
    private readonly object _rejoinGate = new();
    private int _pendingRejoinAttempt; // 0 = none pending
    private int _pendingRejoinMax;
    private int _lastProcessedAttempt;
    // Set when the server ends the session out from under a running seed (give-up / drop);
    // the monitor loop consumes it to close the game and surface a message. Guarded by _rejoinGate.
    private string? _abortReason;

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

    // ── Restart-and-rejoin (server-driven stalled-join recovery) ───────────────

    /// <summary>Whether a restart-and-rejoin is pending (set by <see cref="RequestRejoin"/>,
    /// consumed by <see cref="TakePendingRejoin"/>).</summary>
    public bool IsRejoinRequested
    {
        get { lock (_rejoinGate) { return _pendingRejoinAttempt > 0; } }
    }

    /// <summary>Queue a restart-and-rejoin for the given 1-based <paramref name="attempt"/>. Deduped:
    /// a repeat of an attempt already queued or already processed is ignored (the same attempt can
    /// arrive on several heartbeats). Returns true only when this newly queues a restart.</summary>
    public bool RequestRejoin(int attempt, int maxAttempts)
    {
        if (attempt <= 0)
        {
            return false;
        }
        lock (_rejoinGate)
        {
            if (attempt <= _lastProcessedAttempt || attempt <= _pendingRejoinAttempt)
            {
                return false;
            }
            _pendingRejoinAttempt = attempt;
            _pendingRejoinMax = maxAttempts;
            return true;
        }
    }

    /// <summary>Atomically take the pending restart-and-rejoin, marking that attempt processed so a
    /// duplicate signal won't re-trigger it. Returns <c>(0, 0)</c> when nothing is pending.</summary>
    public (int Attempt, int MaxAttempts) TakePendingRejoin()
    {
        lock (_rejoinGate)
        {
            var attempt = _pendingRejoinAttempt;
            if (attempt == 0)
            {
                return (0, 0);
            }
            var max = _pendingRejoinMax;
            _pendingRejoinAttempt = 0;
            _lastProcessedAttempt = attempt;
            return (attempt, max);
        }
    }

    /// <summary>Clear all restart-and-rejoin state. Called on a new session and whenever the client is
    /// confirmed back on the server (a validated heartbeat), so a later stall starts a fresh cycle.</summary>
    public void ResetRejoin()
    {
        lock (_rejoinGate)
        {
            _pendingRejoinAttempt = 0;
            _pendingRejoinMax = 0;
            _lastProcessedAttempt = 0;
        }
    }

    /// <summary>Whether a restart-and-rejoin cycle is underway (at least one restart asked, not yet
    /// resolved by a validated heartbeat). Lets a server session-end be labelled a join failure.</summary>
    public bool HasActiveRejoinCycle
    {
        get { lock (_rejoinGate) { return _lastProcessedAttempt > 0; } }
    }

    // ── Server-ended-session abort (join give-up / drop) ───────────────────────

    /// <summary>Whether the server has ended the session under a running seed.</summary>
    public bool IsAbortRequested
    {
        get { lock (_rejoinGate) { return _abortReason is not null; } }
    }

    /// <summary>Record that the server ended the session (with a reason). Idempotent: returns true only
    /// on the first request until it is taken/cleared, so repeated heartbeat errors don't stack.</summary>
    public bool RequestAbort(string reason)
    {
        lock (_rejoinGate)
        {
            if (_abortReason is not null)
            {
                return false;
            }
            _abortReason = reason;
            return true;
        }
    }

    /// <summary>Atomically read and clear the abort reason (null if none pending).</summary>
    public string? TakeAbortReason()
    {
        lock (_rejoinGate)
        {
            var reason = _abortReason;
            _abortReason = null;
            return reason;
        }
    }

    /// <summary>Clear any pending abort (new session).</summary>
    public void ClearAbort()
    {
        lock (_rejoinGate) { _abortReason = null; }
    }
}
