using Fullobby.Core.Api;

namespace Fullobby.Core.Seeding;

/// <summary>
/// The servers the user's networks want seeded right now, read off the streamed seeding status —
/// no request of our own. The stream is one broadcast of every network, so it's narrowed to the
/// networks the user belongs to and the games this machine can launch; a change in that set (a
/// new target) is what the idle client reacts to.
/// </summary>
public static class SeedTargets
{
    /// <summary>The current candidates of the user's active networks, for the given games, in the
    /// order the status lists them. Pure; public for unit coverage.</summary>
    public static IReadOnlyList<SeedingCandidate> For(
        SeedingStatusResponse status, IReadOnlySet<long> memberNetworkIds, IReadOnlyCollection<string> games)
    {
        var result = new List<SeedingCandidate>();
        foreach (var n in status.Networks)
        {
            if (!n.Active || !memberNetworkIds.Contains(n.NetworkId))
            {
                continue;
            }
            foreach (var c in new[] { n.Hll, n.Hllv })
            {
                if (c is not null && games.Contains(c.Game))
                {
                    result.Add(c);
                }
            }
        }
        return result;
    }

    /// <summary>Targets in <paramref name="current"/> that weren't in <paramref name="previous"/>
    /// (by server).</summary>
    public static IReadOnlyList<SeedingCandidate> Added(
        IReadOnlySet<long> previous, IReadOnlyList<SeedingCandidate> current) =>
        current.Where(c => !previous.Contains(c.DbId)).ToList();

    /// <summary>The one game the targets point at, or null when there are none or they span both
    /// games (which comes first across games is the directive's call, not knowable from here).</summary>
    public static string? SoleGame(IReadOnlyList<SeedingCandidate> current)
    {
        var games = current.Select(c => c.Game).Distinct().ToList();
        return games.Count == 1 ? games[0] : null;
    }
}
