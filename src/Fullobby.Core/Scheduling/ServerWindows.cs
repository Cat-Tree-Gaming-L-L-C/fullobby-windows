using Fullobby.Core.Api;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// Per-server seed windows as the client sees them: the <see cref="ServerDayStatus.WindowStartTs"/>
/// the seeding status carries for every windowed server, all day, whether or not its ready check
/// has opened yet.
///
/// <para><b>Why this exists.</b> A network's hours say when the rotation runs; a server's own
/// window says when <i>it</i> may be seeded, and it is usually later. The auto-seed wake used to be
/// derived from the network hours alone, so a client woke at the network's opening, found every
/// remaining server still waiting on its window, was told "all exhausted" by the directive, and
/// went back to sleep for the day — the server's window then opened to an empty client fleet.
/// Server windows are daily (a UTC minute-of-day, like network windows), so each one is a
/// legitimate daily wake in its own right; this is the pure extraction the planner and the
/// "nothing to seed yet" messaging share.</para>
/// </summary>
public static class ServerWindows
{
    /// <summary>
    /// The distinct daily UTC window-start times of every windowed server on this network's day
    /// boards, across games. A board entry without a window (null start) contributes nothing.
    /// Seconds are dropped — windows are minute-granular and a wake's identity is "HH:MM".
    /// </summary>
    public static IEnumerable<TimeOnly> StartTimesUtc(NetworkSeedingStatus network)
    {
        var seen = new SortedSet<TimeOnly>();
        foreach (var day in Boards(network))
        {
            if (day.WindowStartTs is { } ts)
            {
                seen.Add(ToTimeUtc(ts));
            }
        }
        return seen;
    }

    /// <summary>
    /// The earliest server window still ahead of <paramref name="nowUnix"/> for
    /// <paramref name="gameId"/>, across the given networks — or null when no window opens later
    /// today. Servers already settled for the day (<see cref="DayStatus.Done"/>,
    /// <see cref="DayStatus.MissedReady"/>) are ignored: their window start being in the future
    /// would be a clock oddity, not a seed that is still coming.
    /// </summary>
    public static long? NextOpeningTs(
        IReadOnlyList<NetworkSeedingStatus>? networks, string gameId, long nowUnix)
    {
        if (networks is null)
        {
            return null;
        }

        long? best = null;
        foreach (var network in networks)
        {
            foreach (var day in Board(network, gameId))
            {
                if (day.WindowStartTs is not { } ts || ts <= nowUnix)
                {
                    continue;
                }
                if (day.Status is DayStatus.Done or DayStatus.MissedReady)
                {
                    continue;
                }
                if (best is null || ts < best)
                {
                    best = ts;
                }
            }
        }
        return best;
    }

    /// <summary>The UTC time-of-day of a unix timestamp, to the minute.</summary>
    public static TimeOnly ToTimeUtc(long unixTs)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(unixTs).UtcDateTime;
        return new TimeOnly(t.Hour, t.Minute);
    }

    /// <summary>One game's day board on a network (empty for an unknown game).</summary>
    public static List<ServerDayStatus> Board(NetworkSeedingStatus network, string gameId) =>
        gameId switch
        {
            "hll" => network.HllDay,
            "hllv" => network.HllvDay,
            _ => [],
        };

    private static IEnumerable<ServerDayStatus> Boards(NetworkSeedingStatus network) =>
        network.HllDay.Concat(network.HllvDay);
}
