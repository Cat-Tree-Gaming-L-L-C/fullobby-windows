using ChllSeeder.Core.Seeding;

namespace ChllSeeder.Core.Tests;

/// <summary>Port of the stop/snooze/switch-now lifecycle tests in <c>backend/seeding.rs</c>.
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
}
