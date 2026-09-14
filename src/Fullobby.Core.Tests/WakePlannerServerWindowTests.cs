using Fullobby.Core.Api;
using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

/// <summary>Server seed windows join the wake set: one daily wake per window start, attributed to
/// the owning network, merged with the network-hours wakes by time.</summary>
public class WakePlannerServerWindowTests
{
    // 2026-09-14 00:00:00 UTC
    private const long Midnight = 1_789_257_600;

    private static long At(int hour, int minute = 0) => Midnight + hour * 3600 + minute * 60;

    private static ServerDayStatus Windowed(long dbId, long startTs) =>
        new() { DbId = dbId, Name = $"s{dbId}", ShortName = $"s{dbId}", Status = DayStatus.NotReady, WindowStartTs = startTs };

    private static ServerDayStatus Unwindowed(long dbId) =>
        new() { DbId = dbId, Name = $"s{dbId}", ShortName = $"s{dbId}", Status = DayStatus.Pending };

    private static TimeWindow Window(int startMin, int endMin) =>
        new() { StartMin = startMin, EndMin = endMin };

    [Fact]
    public void DormantMode_ServerWindowAddsAnAttributedWake_BesideTheFleetWake()
    {
        // The reported case: network hours open at 06:00 (fleet-derived, no per-network fields
        // yet), one server's own window opens at 07:30. The client must wake for both.
        var fleet = new SeedingConfig { ActiveWindows = [Window(360, 540)], MissedAutoseedWindowHours = 4 };
        var network = new NetworkSeedingStatus
        {
            NetworkId = 7,
            NetworkTag = "pf",
            ActiveWindows = null,
            HllDay = [Unwindowed(1), Windowed(2, At(7, 30))],
        };

        var plan = WakePlanner.ComputePlan([network], fleet);

        Assert.Equal(2, plan.Wakes.Count);
        Assert.Equal(new TimeOnly(6, 0), plan.Wakes[0].TimeUtc);
        Assert.Empty(plan.Wakes[0].NetworkIds);                 // still the unattributed fleet wake
        Assert.Equal(new TimeOnly(7, 30), plan.Wakes[1].TimeUtc);
        Assert.Equal([7L], plan.Wakes[1].NetworkIds);           // the server's network justifies it
        Assert.Equal(4, plan.Wakes[1].MissedWindowHours);       // fleet missed window (network omits it)
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void ServerWindowCoincidingWithNetworkOpening_MergesIntoOneWake()
    {
        var fleet = new SeedingConfig();
        var network = new NetworkSeedingStatus
        {
            NetworkId = 1,
            NetworkTag = "a",
            ActiveWindows = [Window(360, 540)],
            MissedAutoseedWindowHours = 6,
            HllDay = [Windowed(1, At(6, 0))],
        };

        var wake = Assert.Single(WakePlanner.ComputePlan([network], fleet).Wakes);
        Assert.Equal(new TimeOnly(6, 0), wake.TimeUtc);
        Assert.Equal([1L], wake.NetworkIds);
        Assert.Equal(6, wake.MissedWindowHours);
    }

    [Fact]
    public void ServerWindows_UseTheirNetworkMissedWindow_AndSortByTime()
    {
        var fleet = new SeedingConfig { MissedAutoseedWindowHours = 4 };
        var a = new NetworkSeedingStatus
        {
            NetworkId = 1, NetworkTag = "a", ActiveWindows = [Window(600, 700)], MissedAutoseedWindowHours = 2,
            HllDay = [Windowed(1, At(18, 0))],
        };
        var b = new NetworkSeedingStatus
        {
            NetworkId = 2, NetworkTag = "b", ActiveWindows = [Window(600, 700)],
            HllDay = [Windowed(2, At(12, 15))],
        };

        var plan = WakePlanner.ComputePlan([a, b], fleet);

        Assert.Equal(
            [new TimeOnly(10, 0), new TimeOnly(12, 15), new TimeOnly(18, 0)],
            plan.Wakes.Select(w => w.TimeUtc).ToList());
        Assert.Equal([1L, 2L], plan.Wakes[0].NetworkIds);   // shared network opening
        Assert.Equal([2L], plan.Wakes[1].NetworkIds);
        Assert.Equal(4, plan.Wakes[1].MissedWindowHours);   // b omits → fleet
        Assert.Equal([1L], plan.Wakes[2].NetworkIds);
        Assert.Equal(2, plan.Wakes[2].MissedWindowHours);   // a's own
    }

    [Fact]
    public void ServerWindows_CountTowardTheCap()
    {
        var fleet = new SeedingConfig { ActiveWindows = [Window(360, 540)] };
        var network = new NetworkSeedingStatus
        {
            NetworkId = 1,
            NetworkTag = "a",
            HllDay = Enumerable.Range(0, 5).Select(i => Windowed(i + 1, At(8 + i * 2))).ToList(),
        };

        var plan = WakePlanner.ComputePlan([network], fleet, maxWakes: 4);

        Assert.Equal(4, plan.Wakes.Count);
        Assert.Equal(new TimeOnly(6, 0), plan.Wakes[0].TimeUtc);       // fleet opening kept first
        Assert.Equal(2, plan.Dropped.Count);
        Assert.Equal(new TimeOnly(14, 0), plan.Dropped[0].TimeUtc);
    }

    [Fact]
    public void BoardsWithoutWindows_ChangeNothing()
    {
        var fleet = new SeedingConfig { ActiveWindows = [Window(360, 540)] };
        var network = new NetworkSeedingStatus
        {
            NetworkId = 1, NetworkTag = "a", HllDay = [Unwindowed(1), Unwindowed(2)],
        };

        var wake = Assert.Single(WakePlanner.ComputePlan([network], fleet).Wakes);
        Assert.Equal(new TimeOnly(6, 0), wake.TimeUtc);
        Assert.Empty(wake.NetworkIds);
    }
}
