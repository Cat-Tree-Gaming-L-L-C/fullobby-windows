using ChllSeeder.Core.Scheduling;

namespace ChllSeeder.Core.Tests;

public class AutoSeedStateTests
{
    [Fact]
    public void TryBegin_IsExclusiveUntilEnded()
    {
        var s = new AutoSeedState();
        Assert.True(s.TryBegin());
        Assert.False(s.TryBegin()); // already in progress
        Assert.True(s.IsInProgress);

        s.End();
        Assert.False(s.IsInProgress);
        Assert.True(s.TryBegin()); // available again
    }

    [Fact]
    public void Cancel_SetsFlagAndReleases()
    {
        var s = new AutoSeedState();
        Assert.True(s.TryBegin());
        Assert.False(s.IsCancelled);

        s.Cancel();
        Assert.True(s.IsCancelled);
        Assert.False(s.IsInProgress);

        // A fresh begin clears the cancel flag.
        Assert.True(s.TryBegin());
        Assert.False(s.IsCancelled);
    }

    [Fact]
    public void TriggeredToday_TracksPerRegionPerDay()
    {
        var s = new AutoSeedState();
        var today = new DateOnly(2026, 6, 10);

        Assert.False(s.WasTriggeredToday("na", today));
        s.RecordTriggered("na", today);
        Assert.True(s.WasTriggeredToday("na", today));
        Assert.False(s.WasTriggeredToday("eu", today)); // independent regions
    }

    [Fact]
    public void RecordTriggered_PrunesStaleDays()
    {
        var s = new AutoSeedState();
        var yesterday = new DateOnly(2026, 6, 9);
        var today = new DateOnly(2026, 6, 10);

        s.RecordTriggered("na", yesterday);
        Assert.True(s.WasTriggeredToday("na", yesterday));

        // Recording on a new day drops the previous day's entries.
        s.RecordTriggered("eu", today);
        Assert.False(s.WasTriggeredToday("na", yesterday));
        Assert.True(s.WasTriggeredToday("eu", today));
    }
}
