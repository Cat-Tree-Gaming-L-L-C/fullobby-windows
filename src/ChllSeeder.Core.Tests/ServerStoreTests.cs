using ChllSeeder.Core.Api;
using ChllSeeder.Core.Servers;

namespace ChllSeeder.Core.Tests;

/// <summary>Port of the server.rs #[test] coverage. Instance-based here, so no
/// global-state cleanup is needed.</summary>
public class ServerStoreTests
{
    private static ServerInfo Make(string name) => new()
    {
        Ip = "127.0.0.1",
        ShortName = name,
        Name = name,
        SeedingThreshold = 50,
        Game = "hll",
    };

    private static ServerStore Populated()
    {
        var store = new ServerStore();
        store.Load(new ServersResponse
        {
            Hll = new RegionServers
            {
                Na = [Make("NA-1"), Make("NA-2")],
                Eu = [Make("EU-1")],
            },
        });
        return store;
    }

    [Fact]
    public void GetServerByRegion_Na()
    {
        var store = Populated();
        Assert.Equal("NA-1", store.GetServerByRegion("na", 0)!.ShortName);
        Assert.Equal("NA-2", store.GetServerByRegion("na", 1)!.ShortName);
    }

    [Fact]
    public void GetServerByRegion_Eu() =>
        Assert.Equal("EU-1", Populated().GetServerByRegion("eu", 0)!.ShortName);

    [Fact]
    public void GetServerByRegion_OutOfBounds_Null()
    {
        var store = Populated();
        Assert.Null(store.GetServerByRegion("na", 99));
        Assert.Null(store.GetServerByRegion("eu", 1));
    }

    [Fact]
    public void GetServerByRegion_NonEu_DefaultsToNa() =>
        Assert.Equal("NA-1", Populated().GetServerByRegion("us", 0)!.ShortName);

    [Fact]
    public void GetServers_Counts()
    {
        var store = Populated();
        Assert.Equal(2, store.GetServers().Count);
        Assert.Single(store.GetEuServers());
    }

    [Fact]
    public void GameServers_Lookup()
    {
        var store = new ServerStore();
        store.Load(new ServersResponse
        {
            Hll = new RegionServers
            {
                Na = [Make("HLL-NA-1")],
                Eu = [Make("HLL-EU-1"), Make("HLL-EU-2")],
            },
        });

        Assert.Equal("HLL-NA-1", store.GetGameServer("hll", "na", 0)!.ShortName);
        Assert.Equal("HLL-EU-2", store.GetGameServer("hll", "eu", 1)!.ShortName);
        Assert.Null(store.GetGameServer("cs2", "na", 0));
        Assert.Null(store.GetGameServer("hll", "oceania", 0));
        Assert.Null(store.GetGameServer("hll", "na", 99));
        Assert.Equal(2, store.GetGameServers("hll", "eu").Count);
        Assert.Empty(store.GetGameServers("cs2", "na"));
        Assert.Empty(store.GetGameServers("hll", "oceania"));
    }

    [Fact]
    public void OfflineTracking()
    {
        var store = Populated();
        store.MarkOffline("NA-1");
        Assert.True(store.IsOffline("NA-1"));
        store.ClearOffline("NA-1");
        Assert.False(store.IsOffline("NA-1"));

        store.MarkOffline("NA-1");
        store.MarkOffline("EU-1");
        store.ClearAllOffline();
        Assert.False(store.IsOffline("NA-1"));
        Assert.False(store.IsOffline("EU-1"));
    }

    [Fact]
    public void PlayerCountTracking()
    {
        var store = Populated();
        Assert.Null(store.GetPlayerCount("NA-1"));

        store.UpdatePlayerCount("NA-1", 42, 100);
        Assert.Equal((42, 100), store.GetPlayerCount("NA-1"));

        store.UpdatePlayerCount("NA-1", 90, 100); // overwrite
        Assert.Equal((90, 100), store.GetPlayerCount("NA-1"));

        store.UpdatePlayerCount("EU-1", 10, 100);
        Assert.Equal((10, 100), store.GetPlayerCount("EU-1"));
        Assert.Equal((90, 100), store.GetPlayerCount("NA-1"));
        Assert.Null(store.GetPlayerCount("NOPE"));
    }
}
