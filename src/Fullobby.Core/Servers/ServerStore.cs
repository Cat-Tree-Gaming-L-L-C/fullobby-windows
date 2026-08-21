using Fullobby.Core.Api;

namespace Fullobby.Core.Servers;

/// <summary>
/// In-memory store of the seeding server lists, offline flags, and live player
/// counts. DI singleton, thread-safe.
/// </summary>
public sealed class ServerStore
{
    private readonly object _gate = new();

    // game -> ordered server rotation (region removed)
    private readonly Dictionary<string, List<ServerInfo>> _gameServers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Players, int MaxPlayers)> _playerCounts = new(StringComparer.Ordinal);

    /// <summary>Replace the per-game server rotations from a /api/servers response. Returns true
    /// when the rotation actually moved (a server added, removed, reordered, or re-thresholded),
    /// so callers can rebuild the UI rows only when it matters — the periodic refresh re-fetches
    /// an identical list most of the time, and rebuilding on every fetch flickers the launch
    /// buttons the rows exist to keep stable.</summary>
    public bool Load(ServersResponse response)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, List<ServerInfo>>(StringComparer.Ordinal)
            {
                ["hll"] = [.. response.Hll],
            };
            if (response.Hllv is { } hllv)
            {
                next["hllv"] = [.. hllv];
            }

            if (SameRotations(_gameServers, next))
            {
                return false;
            }

            _gameServers.Clear();
            foreach (var (game, servers) in next)
            {
                _gameServers[game] = servers;
            }

            // Counts and offline flags are keyed by name, but a server that left the rotation can
            // never be refreshed again — drop it rather than let a frozen count sit in the store
            // answering for a name that may return at a different position.
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var servers in next.Values)
            {
                foreach (var s in servers)
                {
                    live.Add(s.Name);
                }
            }
            _offline.RemoveWhere(n => !live.Contains(n));
            foreach (var name in _playerCounts.Keys.Where(n => !live.Contains(n)).ToList())
            {
                _playerCounts.Remove(name);
            }

            return true;
        }
    }

    /// <summary>Sequence-compare two rotation sets. Position matters: the API keys every stats
    /// batch and every seeding directive by index into the game's rotation, so a reorder is as
    /// meaningful a change as an insertion.</summary>
    private static bool SameRotations(
        Dictionary<string, List<ServerInfo>> a,
        Dictionary<string, List<ServerInfo>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var (game, left) in a)
        {
            if (!b.TryGetValue(game, out var right) || left.Count != right.Count)
            {
                return false;
            }
            for (var i = 0; i < left.Count; i++)
            {
                if (!SameServer(left[i], right[i]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool SameServer(ServerInfo a, ServerInfo b) =>
        a.Name == b.Name
        && a.ShortName == b.ShortName
        && a.Ip == b.Ip
        && a.Game == b.Game
        && a.BmId == b.BmId
        && a.SeedingThreshold == b.SeedingThreshold;

    /// <summary>Get a server by game and index in its rotation, or null when out of range.</summary>
    public ServerInfo? GetServer(string game, int index)
    {
        lock (_gate)
        {
            return _gameServers.TryGetValue(game, out var servers)
                && index >= 0 && index < servers.Count
                ? servers[index]
                : null;
        }
    }

    /// <summary>The ordered server rotation for a game (defaults to HLL).</summary>
    public IReadOnlyList<ServerInfo> GetServers(string game = "hll")
    {
        lock (_gate)
        {
            return _gameServers.TryGetValue(game, out var servers) ? [.. servers] : [];
        }
    }

    public void MarkOffline(string serverName)
    {
        lock (_gate) { _offline.Add(serverName); }
    }

    public void ClearOffline(string serverName)
    {
        lock (_gate) { _offline.Remove(serverName); }
    }

    public void ClearAllOffline()
    {
        lock (_gate) { _offline.Clear(); }
    }

    public bool IsOffline(string serverName)
    {
        lock (_gate) { return _offline.Contains(serverName); }
    }

    public void UpdatePlayerCount(string serverName, int players, int maxPlayers)
    {
        lock (_gate) { _playerCounts[serverName] = (players, maxPlayers); }
    }

    /// <summary>Last-known (players, maxPlayers) for a server, or null if unknown.</summary>
    public (int Players, int MaxPlayers)? GetPlayerCount(string serverName)
    {
        lock (_gate)
        {
            return _playerCounts.TryGetValue(serverName, out var counts) ? counts : null;
        }
    }
}
