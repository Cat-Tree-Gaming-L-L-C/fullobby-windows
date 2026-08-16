using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

public class MissedAutoseedMonitorTests
{
    private static DateTime Utc(int h, int m) => new(2026, 6, 10, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void WithinWindow_JustAfterScheduled_Fires()
    {
        Assert.True(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(9, 15), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void WithinWindow_JustInsideWindowEdge_Fires()
    {
        // 3h59m past is inside [0, 4h).
        Assert.True(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(12, 59), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void WithinWindow_ExactlyAtWindowEdge_DoesNotFire()
    {
        // Exactly 4h past is the exclusive upper bound (>= 4h is excluded).
        Assert.False(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(13, 0), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void WithinWindow_PastWindow_DoesNotFire()
    {
        Assert.False(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(13, 1), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void WithinWindow_BeforeScheduled_DoesNotFire()
    {
        Assert.False(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(8, 59), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void WithinWindow_ExactlyAtScheduled_Fires()
    {
        Assert.True(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(9, 0), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void Eligible_OnboardedAndIdle_Fires()
    {
        Assert.True(MissedAutoseedMonitor.IsEligibleToFire(
            onboardingComplete: true, inProgress: false, triggeredToday: false));
    }

    [Fact]
    public void Eligible_OnboardingIncomplete_DoesNotFire()
    {
        // A scheduled task left behind by a previous install (or an auth reset that re-armed the
        // wizard) must never notify or launch the game at a first-run app.
        Assert.False(MissedAutoseedMonitor.IsEligibleToFire(
            onboardingComplete: false, inProgress: false, triggeredToday: false));
    }

    [Fact]
    public void Eligible_OnboardingIncomplete_OutranksEveryOtherCheck()
    {
        Assert.False(MissedAutoseedMonitor.IsEligibleToFire(
            onboardingComplete: false, inProgress: true, triggeredToday: true));
    }

    [Fact]
    public void Eligible_AlreadyInProgress_DoesNotFire()
    {
        Assert.False(MissedAutoseedMonitor.IsEligibleToFire(
            onboardingComplete: true, inProgress: true, triggeredToday: false));
    }

    [Fact]
    public void Eligible_AlreadyTriggeredToday_DoesNotFire()
    {
        Assert.False(MissedAutoseedMonitor.IsEligibleToFire(
            onboardingComplete: true, inProgress: false, triggeredToday: true));
    }
}
