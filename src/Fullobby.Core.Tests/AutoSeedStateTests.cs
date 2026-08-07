using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

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
    public void TriggeredToday_TracksPerDay()
    {
        var s = new AutoSeedState();
        var today = new DateOnly(2026, 6, 10);

        Assert.False(s.WasTriggeredToday(today));
        s.RecordTriggered(today);
        Assert.True(s.WasTriggeredToday(today));
    }

    [Fact]
    public void RecordTriggered_OnlyMatchesRecordedDay()
    {
        var s = new AutoSeedState();
        var yesterday = new DateOnly(2026, 6, 9);
        var today = new DateOnly(2026, 6, 10);

        s.RecordTriggered(yesterday);
        Assert.True(s.WasTriggeredToday(yesterday));

        // Recording on a new day means the previous day no longer matches.
        s.RecordTriggered(today);
        Assert.False(s.WasTriggeredToday(yesterday));
        Assert.True(s.WasTriggeredToday(today));
    }
}
