using ChllSeeding.Core.Scheduling;

namespace ChllSeeding.Core.Tests;

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
        // 3h59m past — still within the 4h window.
        Assert.True(MissedAutoseedMonitor.IsWithinMissedWindow(Utc(12, 59), new TimeOnly(9, 0), 4));
    }

    [Fact]
    public void WithinWindow_ExactlyAtWindowEdge_DoesNotFire()
    {
        // Exactly 4h past — exclusive upper bound, matching Rust's num_hours() >= 4 skip.
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
}
