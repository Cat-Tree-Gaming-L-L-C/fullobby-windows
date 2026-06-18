using ChllSeeding.Core.Api;

namespace ChllSeeding.Core.Servers;

/// <summary>
/// The single sink for live server stats, fed by both producers — the SSE
/// <c>stats</c> stream (primary) and the HTTP polling fallback. Pushes fresh
/// counts/offline flags into the <see cref="ServerStore"/> (so the seeding
/// engine's fill-based stagger sees live data) and raises <see cref="StatsUpdated"/>
/// for the UI banner. Port of <c>app::apply_stats_update</c> in the Rust app.
/// DI singleton; <see cref="Apply"/> runs on background threads, so subscribers
/// must marshal to the UI thread themselves.
/// </summary>
public sealed class LiveStats(ServerStore servers)
{
    /// <summary>Raised after each stats batch is applied, carrying the freshest batch.</summary>
    public event Action<IReadOnlyList<BatchStatsResult>>? StatsUpdated;

    /// <summary>UTC time of the most recent applied stats batch, or <c>null</c> before the first
    /// update. Drives the "Updated Ns ago" / stale indicator (port of the Rust
    /// <c>LAST_STATS_UPDATE</c> signal).</summary>
    public DateTime? LastUpdateUtc { get; private set; }

    /// <summary>Apply a stats batch to the store and notify subscribers.</summary>
    public void Apply(IReadOnlyList<BatchStatsResult> stats)
    {
        foreach (var s in stats)
        {
            var server = servers.GetGameServer(s.Game, s.Region, s.Index);
            if (server is null)
            {
                continue;
            }
            if (s.Offline)
            {
                servers.MarkOffline(server.Name);
            }
            else
            {
                servers.ClearOffline(server.Name);
            }
            if (s.PlayerCount is { } players)
            {
                servers.UpdatePlayerCount(server.Name, players, s.MaxPlayerCount ?? 100);
            }
        }

        LastUpdateUtc = DateTime.UtcNow;
        StatsUpdated?.Invoke(stats);
    }
}
