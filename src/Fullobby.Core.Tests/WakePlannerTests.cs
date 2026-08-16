using Fullobby.Core.Api;
using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

/// <summary>The pure wake-set derivation (docs/PER-TENANT-SCHEDULING.md): per-network windows with
/// fleet fallback, dedup by time, missed-window max, and the wake-count cap.</summary>
public class WakePlannerTests
{
    private static NetworkSeedingStatus Network(
        long id, List<TimeWindow>? windows, int? resetHour = null, int? missedHours = null) =>
        new()
        {
            NetworkId = id,
            NetworkTag = $"net{id}",
            ActiveWindows = windows,
            DailyResetHourUtc = resetHour,
            MissedAutoseedWindowHours = missedHours,
        };

    private static TimeWindow Window(int startMin, int endMin) =>
        new() { StartMin = startMin, EndMin = endMin };

    // ── Dormant / fallback mode ─────────────────────────────────────────────

    [Fact]
    public void NoNetworks_YieldsSingleUnattributedFleetWake()
    {
        var fleet = new SeedingConfig
        {
            ActiveWindows = [Window(360, 540)],
            MissedAutoseedWindowHours = 4,
        };
        var plan = WakePlanner.ComputePlan([], fleet);

        var wake = Assert.Single(plan.Wakes);
        Assert.Equal(new TimeOnly(6, 0), wake.TimeUtc);
        Assert.Empty(wake.NetworkIds);
        Assert.Equal(4, wake.MissedWindowHours);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void NetworksWithoutBoundaries_StayOnTheSingleFleetWake()
    {
        // The backend hasn't shipped the per-tenant fields (ActiveWindows null everywhere):
        // exactly the pre-per-tenant behaviour, no attribution.
        var fleet = new SeedingConfig { ActiveWindows = [Window(600, 700)] };
        var plan = WakePlanner.ComputePlan(
            [Network(1, windows: null), Network(2, windows: null)], fleet);

        var wake = Assert.Single(plan.Wakes);
        Assert.Equal(new TimeOnly(10, 0), wake.TimeUtc);
        Assert.Empty(wake.NetworkIds);
    }

    // ── Per-network windows ─────────────────────────────────────────────────

    [Fact]
    public void DisjointNetworks_GetOneWakeEach()
    {
        var fleet = new SeedingConfig();
        var plan = WakePlanner.ComputePlan(
            [Network(1, [Window(360, 540)]), Network(2, [Window(1320, 60)])], fleet);

        Assert.Equal(2, plan.Wakes.Count);
        Assert.Equal(new TimeOnly(6, 0), plan.Wakes[0].TimeUtc);
        Assert.Equal([1L], plan.Wakes[0].NetworkIds);
        Assert.Equal(new TimeOnly(22, 0), plan.Wakes[1].TimeUtc);
        Assert.Equal([2L], plan.Wakes[1].NetworkIds);
    }

    [Fact]
    public void CoincidingWindows_CollapseToOneWakeJustifiedByBoth()
    {
        // Two networks sharing 06:00 wake the machine once, not twice.
        var fleet = new SeedingConfig();
        var plan = WakePlanner.ComputePlan(
            [Network(2, [Window(360, 540)]), Network(1, [Window(360, 700)])], fleet);

        var wake = Assert.Single(plan.Wakes);
        Assert.Equal(new TimeOnly(6, 0), wake.TimeUtc);
        Assert.Equal([1L, 2L], wake.NetworkIds); // sorted, both justify it
    }

    [Fact]
    public void EmptyWindowList_MeansAlwaysActive_WakesAtDailyReset()
    {
        var fleet = new SeedingConfig { DailyResetHourUtc = 10 };
        var plan = WakePlanner.ComputePlan([Network(1, [])], fleet);

        var wake = Assert.Single(plan.Wakes);
        Assert.Equal(new TimeOnly(10, 0), wake.TimeUtc);
        Assert.Equal([1L], wake.NetworkIds);
    }

    [Fact]
    public void EmptyWindowList_PrefersTheNetworkOwnResetHour()
    {
        var fleet = new SeedingConfig { DailyResetHourUtc = 10 };
        var plan = WakePlanner.ComputePlan([Network(1, [], resetHour: 7)], fleet);

        Assert.Equal(new TimeOnly(7, 0), Assert.Single(plan.Wakes).TimeUtc);
    }

    [Fact]
    public void NetworkOmittingWindows_FallsBackToFleetWindows_WithAttribution()
    {
        // One network sends boundaries, the other doesn't: the data-less one is covered by the
        // fleet windows, attributed to it (permanent fallback, not transitional).
        var fleet = new SeedingConfig { ActiveWindows = [Window(600, 700)] };
        var plan = WakePlanner.ComputePlan(
            [Network(1, [Window(360, 540)]), Network(2, windows: null)], fleet);

        Assert.Equal(2, plan.Wakes.Count);
        Assert.Equal([1L], plan.Wakes[0].NetworkIds);       // 06:00 from network 1
        Assert.Equal(new TimeOnly(10, 0), plan.Wakes[1].TimeUtc);
        Assert.Equal([2L], plan.Wakes[1].NetworkIds);       // fleet 10:00 on network 2's behalf
    }

    [Fact]
    public void MultipleWindows_OneWakePerWindowStart()
    {
        var fleet = new SeedingConfig();
        var plan = WakePlanner.ComputePlan(
            [Network(1, [Window(360, 540), Window(1080, 1200)])], fleet);

        Assert.Equal(2, plan.Wakes.Count);
        Assert.Equal(new TimeOnly(6, 0), plan.Wakes[0].TimeUtc);
        Assert.Equal(new TimeOnly(18, 0), plan.Wakes[1].TimeUtc);
    }

    // ── Missed-window resolution ────────────────────────────────────────────

    [Fact]
    public void MissedWindow_TakesTheMaxAcrossJustifyingNetworks()
    {
        // The watchdog guards the machine having slept through the wake — the most patient
        // tenant's window governs when two disagree.
        var fleet = new SeedingConfig { MissedAutoseedWindowHours = 4 };
        var plan = WakePlanner.ComputePlan(
            [Network(1, [Window(360, 540)], missedHours: 2), Network(2, [Window(360, 700)], missedHours: 6)],
            fleet);

        Assert.Equal(6, Assert.Single(plan.Wakes).MissedWindowHours);
    }

    [Fact]
    public void MissedWindow_FallsBackToFleetWhenTheNetworkOmitsIt()
    {
        var fleet = new SeedingConfig { MissedAutoseedWindowHours = 5 };
        var plan = WakePlanner.ComputePlan([Network(1, [Window(360, 540)])], fleet);

        Assert.Equal(5, Assert.Single(plan.Wakes).MissedWindowHours);
    }

    // ── Cap ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Cap_KeepsEarliestWakes_AndReportsTheDropped()
    {
        var fleet = new SeedingConfig();
        var networks = Enumerable.Range(0, 6)
            .Select(i => Network(i + 1, [Window(i * 120, i * 120 + 60)]))
            .ToList();
        var plan = WakePlanner.ComputePlan(networks, fleet, maxWakes: 4);

        Assert.Equal(4, plan.Wakes.Count);
        Assert.Equal(2, plan.Dropped.Count);
        Assert.Equal(new TimeOnly(0, 0), plan.Wakes[0].TimeUtc);
        Assert.Equal(new TimeOnly(8, 0), plan.Dropped[0].TimeUtc);   // 5th wake, dropped
    }

    [Fact]
    public void WindowStartsAreClampedToADay()
    {
        var fleet = new SeedingConfig();
        var plan = WakePlanner.ComputePlan([Network(1, [Window(5000, 100)])], fleet);

        Assert.Equal(new TimeOnly(23, 59), Assert.Single(plan.Wakes).TimeUtc);
    }

    // ── Fleet wake derivation (previously AutoSeedService.WakeTimeUtc) ──────

    [Fact]
    public void FleetWake_NoWindows_UsesDailyResetHour()
    {
        var cfg = new SeedingConfig { ActiveWindows = [], DailyResetHourUtc = 10 };
        Assert.Equal((10, 0), WakePlanner.FleetWakeTimeUtc(cfg));
    }

    [Fact]
    public void FleetWake_UsesEarliestWindowStart()
    {
        var cfg = new SeedingConfig
        {
            ActiveWindows = [Window(1200, 1320), Window(735, 840)],
        };
        Assert.Equal((12, 15), WakePlanner.FleetWakeTimeUtc(cfg));
    }
}
