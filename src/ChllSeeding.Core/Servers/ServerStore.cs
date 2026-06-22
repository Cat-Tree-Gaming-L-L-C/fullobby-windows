using ChllSeeding.Core.Api;

namespace ChllSeeding.Core.Servers;

/// <summary>
/// In-memory store of the seeding server lists, offline flags, and live player
/// counts. Port of <c>src-rust/src/backend/server.rs</c> (global statics →
/// instance state). DI singleton, thread-safe.
/// </summary>
public sealed class ServerStore
{
    private readonly object _gate = new();

    // game -> ordered server rotation (region removed)
    private readonly Dictionary<string, List<ServerInfo>> _gameServers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Players, int MaxPlayers)> _playerCounts = new(StringComparer.Ordinal);

    /// <summary>Replace the per-game server rotations from a /api/servers response.</summary>
    public void Load(ServersResponse response)
    {
        lock (_gate)
        {
            _gameServers.Clear();
            _gameServers["hll"] = [.. response.Hll];
            if (response.Hllv is { } hllv)
            {
                _gameServers["hllv"] = [.. hllv];
            }
        }
    }

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
