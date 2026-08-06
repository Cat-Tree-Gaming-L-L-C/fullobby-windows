using Fullobby.Core.Seeding;

namespace Fullobby.Core.Tests;

/// <summary>Tests for the stop/snooze/switch-now lifecycle.
/// Instance-based here, so no global-state cleanup between tests is needed.</summary>
public class SeedingStateTests
{
    [Fact] // test_stop_flag_lifecycle
    public void StopFlagLifecycle()
    {
        var state = new SeedingState();
        Assert.False(state.IsStopRequested);

        state.RequestStop();
        Assert.True(state.IsStopRequested);

        state.ClearStop();
        Assert.False(state.IsStopRequested);
    }

    [Fact] // test_snooze_lifecycle
    public void SnoozeLifecycle()
    {
        var state = new SeedingState();
        Assert.False(state.IsSwitchSnoozed);

        state.SetSwitchSnooze(300);
        Assert.True(state.IsSwitchSnoozed);

        Assert.Equal(300, state.TakeSnoozeDuration());
        // After take, the snooze is consumed.
        Assert.False(state.IsSwitchSnoozed);
        Assert.Equal(0, state.TakeSnoozeDuration());

        // SetSwitchSnooze stores at least 1.
        state.SetSwitchSnooze(0);
        Assert.True(state.IsSwitchSnoozed);
        state.ClearSwitchState();
    }

    [Fact] // test_snooze_server_switch_clamping
    public void SnoozeServerSwitchClamping()
    {
        var state = new SeedingState();

        state.SnoozeServerSwitch(10);      // below min → 60
        Assert.Equal(60, state.TakeSnoozeDuration());

        state.SnoozeServerSwitch(2000);    // above max → 1800
        Assert.Equal(1800, state.TakeSnoozeDuration());

        state.SnoozeServerSwitch(500);     // within range → unchanged
        Assert.Equal(500, state.TakeSnoozeDuration());
    }

    [Fact] // test_switch_now_lifecycle
    public void SwitchNowLifecycle()
    {
        var state = new SeedingState();
        Assert.False(state.IsSwitchNowRequested);

        state.RequestSwitchNow();
        Assert.True(state.IsSwitchNowRequested);

        state.ClearSwitchState();
        Assert.False(state.IsSwitchNowRequested);
    }

    // ── Restart-and-rejoin coordination ────────────────────────────────────────

    [Fact]
    public void RejoinRequestAndTakeLifecycle()
    {
        var state = new SeedingState();
        Assert.False(state.IsRejoinRequested);

        Assert.True(state.RequestRejoin(1, 3));
        Assert.True(state.IsRejoinRequested);

        Assert.Equal((1, 3), state.TakePendingRejoin());
        // Consumed.
        Assert.False(state.IsRejoinRequested);
        Assert.Equal((0, 0), state.TakePendingRejoin());
    }

    [Fact]
    public void RejoinDedupesSameAttempt()
    {
        var state = new SeedingState();

        // The same attempt delivered on several heartbeats only queues once.
        Assert.True(state.RequestRejoin(1, 3));
        Assert.False(state.RequestRejoin(1, 3));

        Assert.Equal((1, 3), state.TakePendingRejoin());

        // After processing attempt 1, a repeat of attempt 1 is ignored...
        Assert.False(state.RequestRejoin(1, 3));
        Assert.False(state.IsRejoinRequested);

        // ...but the next (higher) attempt still queues.
        Assert.True(state.RequestRejoin(2, 3));
        Assert.Equal((2, 3), state.TakePendingRejoin());
    }

    [Fact]
    public void RejoinIgnoresNonPositiveAttempt()
    {
        var state = new SeedingState();
        Assert.False(state.RequestRejoin(0, 3));
        Assert.False(state.RequestRejoin(-1, 3));
        Assert.False(state.IsRejoinRequested);
    }

    [Fact]
    public void ResetRejoinClearsProcessedHistory()
    {
        var state = new SeedingState();

        Assert.True(state.RequestRejoin(3, 3));
        Assert.Equal((3, 3), state.TakePendingRejoin());

        // Reset (e.g. a validated heartbeat) → a fresh cycle can start again at attempt 1.
        state.ResetRejoin();
        Assert.False(state.IsRejoinRequested);
        Assert.True(state.RequestRejoin(1, 3));
        Assert.Equal((1, 3), state.TakePendingRejoin());
    }

    [Fact]
    public void HasActiveRejoinCycleTracksProcessedAttempts()
    {
        var state = new SeedingState();
        Assert.False(state.HasActiveRejoinCycle);

        state.RequestRejoin(1, 3);
        // Queued but not yet taken → not "active" until processed.
        Assert.False(state.HasActiveRejoinCycle);

        state.TakePendingRejoin();
        Assert.True(state.HasActiveRejoinCycle);

        // A validated heartbeat resolves the cycle.
        state.ResetRejoin();
        Assert.False(state.HasActiveRejoinCycle);
    }

    // ── Server-ended-session abort ─────────────────────────────────────────────

    [Fact]
    public void AbortRequestIsIdempotentUntilTaken()
    {
        var state = new SeedingState();
        Assert.False(state.IsAbortRequested);

        Assert.True(state.RequestAbort("join_failed"));
        Assert.True(state.IsAbortRequested);
        // A second request while one is pending is ignored (repeated heartbeat errors don't stack).
        Assert.False(state.RequestAbort("session_closed"));

        Assert.Equal("join_failed", state.TakeAbortReason());
        Assert.False(state.IsAbortRequested);
        Assert.Null(state.TakeAbortReason());

        // After taking, a new abort can be requested again.
        Assert.True(state.RequestAbort("validation_failed"));
        state.ClearAbort();
        Assert.False(state.IsAbortRequested);
    }
}
