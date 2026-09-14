using Fullobby.Core.Api;
using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

/// <summary>Server seed windows as read off the day boards: the daily wake times they imply and
/// the "next window still ahead today" the nothing-to-seed-yet messaging relies on.</summary>
public class ServerWindowsTests
{
    // 2026-09-14 00:00:00 UTC
    private const long Midnight = 1_789_257_600;

    private static long At(int hour, int minute = 0) => Midnight + hour * 3600 + minute * 60;

    private static ServerDayStatus Day(long dbId, DayStatus status, long? windowStartTs) =>
        new() { DbId = dbId, Name = $"s{dbId}", ShortName = $"s{dbId}", Status = status, WindowStartTs = windowStartTs };

    private static NetworkSeedingStatus Network(long id, List<ServerDayStatus> hll, List<ServerDayStatus>? hllv = null) =>
        new() { NetworkId = id, NetworkTag = $"net{id}", HllDay = hll, HllvDay = hllv ?? [] };

    [Fact]
    public void StartTimes_AreTheDistinctWindowStarts_ToTheMinute()
    {
        var network = Network(1,
        [
            Day(1, DayStatus.NotReady, At(7, 30)),
            Day(2, DayStatus.Pending, null),          // no window → nothing
            Day(3, DayStatus.Done, At(7, 30) + 20),   // same minute as server 1 (seconds dropped)
            Day(4, DayStatus.NotReady, At(18, 0)),
        ]);

        Assert.Equal([new TimeOnly(7, 30), new TimeOnly(18, 0)], ServerWindows.StartTimesUtc(network));
    }

    [Fact]
    public void StartTimes_SpanBothGamesBoards()
    {
        var network = Network(1,
            hll: [Day(1, DayStatus.NotReady, At(7, 30))],
            hllv: [Day(2, DayStatus.NotReady, At(21, 0))]);

        Assert.Equal([new TimeOnly(7, 30), new TimeOnly(21, 0)], ServerWindows.StartTimesUtc(network));
    }

    [Fact]
    public void NextOpening_IsTheEarliestFutureWindow_ForTheGame()
    {
        var networks = new List<NetworkSeedingStatus>
        {
            Network(1, [Day(1, DayStatus.NotReady, At(7, 30)), Day(2, DayStatus.NotReady, At(9, 0))]),
            Network(2, [Day(3, DayStatus.NotReady, At(8, 0))], hllv: [Day(4, DayStatus.NotReady, At(6, 30))]),
        };

        // 06:00 — the 06:30 window is HLLV's, not ours; 07:30 is the first HLL one.
        Assert.Equal(At(7, 30), ServerWindows.NextOpeningTs(networks, "hll", At(6)));
        Assert.Equal(At(6, 30), ServerWindows.NextOpeningTs(networks, "hllv", At(6)));
    }

    [Fact]
    public void NextOpening_IgnoresWindowsAlreadyOpen_AndSettledServers()
    {
        var networks = new List<NetworkSeedingStatus>
        {
            Network(1,
            [
                Day(1, DayStatus.MissedReady, At(7, 30)),    // passed
                Day(2, DayStatus.Done, At(12, 0)),           // future start but already seeded — not a coming seed
                Day(3, DayStatus.MissedReady, At(13, 0)),    // out for the day
                Day(4, DayStatus.NotReady, At(15, 0)),
            ]),
        };

        Assert.Equal(At(15), ServerWindows.NextOpeningTs(networks, "hll", At(8)));
    }

    [Fact]
    public void NextOpening_NullWhenNothingIsAhead()
    {
        var networks = new List<NetworkSeedingStatus>
        {
            Network(1, [Day(1, DayStatus.Done, At(7, 30)), Day(2, DayStatus.Pending, null)]),
        };

        Assert.Null(ServerWindows.NextOpeningTs(networks, "hll", At(8)));
        Assert.Null(ServerWindows.NextOpeningTs(null, "hll", At(8)));
        Assert.Null(ServerWindows.NextOpeningTs([], "hll", At(8)));
    }

    [Fact]
    public void NextOpening_ExactlyNow_CountsAsOpen()
    {
        var networks = new List<NetworkSeedingStatus>
        {
            Network(1, [Day(1, DayStatus.NotReady, At(7, 30))]),
        };

        Assert.Null(ServerWindows.NextOpeningTs(networks, "hll", At(7, 30)));
    }
}
