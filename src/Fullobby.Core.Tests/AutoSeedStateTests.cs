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

        Assert.False(s.WasTriggeredToday("06:00", today));
        s.RecordTriggered("06:00", today);
        Assert.True(s.WasTriggeredToday("06:00", today));
    }

    [Fact]
    public void RecordTriggered_OnlyMatchesRecordedDay()
    {
        var s = new AutoSeedState();
        var yesterday = new DateOnly(2026, 6, 9);
        var today = new DateOnly(2026, 6, 10);

        s.RecordTriggered("06:00", yesterday);
        Assert.True(s.WasTriggeredToday("06:00", yesterday));

        // Recording on a new day means the previous day no longer matches.
        s.RecordTriggered("06:00", today);
        Assert.False(s.WasTriggeredToday("06:00", yesterday));
        Assert.True(s.WasTriggeredToday("06:00", today));
    }

    [Fact]
    public void TriggeredToday_IsPerWake()
    {
        // The whole point of per-wake keying: the 06:00 seed having run must not block the
        // 22:00 wake the same evening.
        var s = new AutoSeedState();
        var today = new DateOnly(2026, 6, 10);

        s.RecordTriggered("06:00", today);
        Assert.True(s.WasTriggeredToday("06:00", today));
        Assert.False(s.WasTriggeredToday("22:00", today));

        s.RecordTriggered("22:00", today);
        Assert.True(s.WasTriggeredToday("22:00", today));
    }

    [Fact]
    public void UnknownWakeKey_IsItsOwnRecord()
    {
        var s = new AutoSeedState();
        var today = new DateOnly(2026, 6, 10);

        s.RecordTriggered(AutoSeedState.UnknownWakeKey, today);
        Assert.True(s.WasTriggeredToday(AutoSeedState.UnknownWakeKey, today));
        Assert.False(s.WasTriggeredToday("06:00", today));
    }
}
