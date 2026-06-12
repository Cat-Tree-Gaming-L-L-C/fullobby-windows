using ChllSeeding.Core.Api;
using ChllSeeding.Core.Servers;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for the shared stats sink fed by both the SSE stream and the poll
/// fallback (port of <c>app::apply_stats_update</c>).</summary>
public class LiveStatsTests
{
    private static ServerInfo Make(string name) => new()
    {
        Ip = "127.0.0.1",
        ShortName = name,
        Name = name,
        SeedingThreshold = 50,
        Game = "hll",
    };

    private static (ServerStore store, LiveStats live) Setup()
    {
        var store = new ServerStore();
        store.Load(new ServersResponse
        {
            Hll = new RegionServers { Na = [Make("NA-1"), Make("NA-2")], Eu = [Make("EU-1")] },
        });
        return (store, new LiveStats(store));
    }

    [Fact]
    public void Apply_UpdatesPlayerCountAndOffline()
    {
        var (store, live) = Setup();

        live.Apply(
        [
            new BatchStatsResult { Game = "hll", Region = "na", Index = 0, PlayerCount = 42, MaxPlayerCount = 100 },
            new BatchStatsResult { Game = "hll", Region = "eu", Index = 0, Offline = true },
        ]);

        Assert.Equal((42, 100), store.GetPlayerCount("NA-1"));
        Assert.True(store.IsOffline("EU-1"));
    }

    [Fact]
    public void Apply_ClearsOfflineWhenBackOnline()
    {
        var (store, live) = Setup();
        store.MarkOffline("NA-1");

        live.Apply([new BatchStatsResult { Game = "hll", Region = "na", Index = 0, PlayerCount = 5, MaxPlayerCount = 100 }]);

        Assert.False(store.IsOffline("NA-1"));
    }

    [Fact]
    public void Apply_UnknownServerIgnored()
    {
        var (store, live) = Setup();
        // Index out of range / unknown game must not throw.
        live.Apply(
        [
            new BatchStatsResult { Game = "hll", Region = "na", Index = 99, PlayerCount = 1 },
            new BatchStatsResult { Game = "cs2", Region = "na", Index = 0, PlayerCount = 1 },
        ]);
        Assert.Null(store.GetPlayerCount("NA-1"));
    }

    [Fact]
    public void Apply_RaisesStatsUpdatedOncePerBatch()
    {
        var (_, live) = Setup();
        var count = 0;
        IReadOnlyList<BatchStatsResult>? received = null;
        live.StatsUpdated += s => { count++; received = s; };

        var batch = new[] { new BatchStatsResult { Game = "hll", Region = "na", Index = 0, PlayerCount = 7 } };
        live.Apply(batch);

        Assert.Equal(1, count);
        Assert.Same(batch, received);
    }
}
