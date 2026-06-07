using ChllSeeder.Core.Api;

namespace ChllSeeder.Core.Servers;

/// <summary>
/// In-memory store of the seeding server lists, offline flags, and live player
/// counts. Port of <c>src-rust/src/backend/server.rs</c> (global statics →
/// instance state). DI singleton, thread-safe.
/// </summary>
public sealed class ServerStore
{
    private readonly object _gate = new();
    private List<ServerInfo> _na = [];
    private List<ServerInfo> _eu = [];

    // game -> region -> servers
    private readonly Dictionary<string, Dictionary<string, List<ServerInfo>>> _gameServers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Players, int MaxPlayers)> _playerCounts = new(StringComparer.Ordinal);

    /// <summary>Replace the NA/EU and game-aware server lists from a /api/servers response.</summary>
    public void Load(ServersResponse response)
    {
        lock (_gate)
        {
            _na = [.. response.Hll.Na];
            _eu = [.. response.Hll.Eu];

            _gameServers.Clear();
            _gameServers["hll"] = new(StringComparer.Ordinal)
            {
                ["na"] = [.. response.Hll.Na],
                ["eu"] = [.. response.Hll.Eu],
            };
            if (response.Hllv is { } hllv)
            {
                _gameServers["hllv"] = new(StringComparer.Ordinal)
                {
                    ["na"] = [.. hllv.Na],
                    ["eu"] = [.. hllv.Eu],
                };
            }
        }
    }

    /// <summary>Get a server by region ("eu" → EU list, anything else → NA) and index,
    /// or null when the index is out of range.</summary>
    public ServerInfo? GetServerByRegion(string region, int index)
    {
        lock (_gate)
        {
            var list = region == "eu" ? _eu : _na;
            return index >= 0 && index < list.Count ? list[index] : null;
        }
    }

    public IReadOnlyList<ServerInfo> GetServers()
    {
        lock (_gate) { return [.. _na]; }
    }

    public IReadOnlyList<ServerInfo> GetEuServers()
    {
        lock (_gate) { return [.. _eu]; }
    }

    public ServerInfo? GetGameServer(string game, string region, int index)
    {
        lock (_gate)
        {
            if (_gameServers.TryGetValue(game, out var regions)
                && regions.TryGetValue(region, out var servers)
                && index >= 0 && index < servers.Count)
            {
                return servers[index];
            }
            return null;
        }
    }

    public IReadOnlyList<ServerInfo> GetGameServers(string game, string region)
    {
        lock (_gate)
        {
            if (_gameServers.TryGetValue(game, out var regions)
                && regions.TryGetValue(region, out var servers))
            {
                return [.. servers];
            }
            return [];
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
