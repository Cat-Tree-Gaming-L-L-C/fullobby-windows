using ChllSeeding.Core.Api;
using ChllSeeding.Core.Servers;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for the server store. Instance-based here, so no
/// global-state cleanup is needed. Region removed — one ordered rotation per game.</summary>
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
            Hll = [Make("HLL-1"), Make("HLL-2"), Make("HLL-3")],
        });
        return store;
    }

    [Fact]
    public void GetServer_ByIndex()
    {
        var store = Populated();
        Assert.Equal("HLL-1", store.GetServer("hll", 0)!.ShortName);
        Assert.Equal("HLL-2", store.GetServer("hll", 1)!.ShortName);
        Assert.Equal("HLL-3", store.GetServer("hll", 2)!.ShortName);
    }

    [Fact]
    public void GetServer_OutOfBounds_Null()
    {
        var store = Populated();
        Assert.Null(store.GetServer("hll", 99));
        Assert.Null(store.GetServer("hll", -1));
    }

    [Fact]
    public void GetServer_UnknownGame_Null() =>
        Assert.Null(Populated().GetServer("cs2", 0));

    [Fact]
    public void GetServers_DefaultsToHll() =>
        Assert.Equal(3, Populated().GetServers().Count);

    [Fact]
    public void GameServers_Lookup()
    {
        var store = new ServerStore();
        store.Load(new ServersResponse
        {
            Hll = [Make("HLL-1")],
            Hllv = [Make("HLLV-1"), Make("HLLV-2")],
        });

        Assert.Equal("HLL-1", store.GetServer("hll", 0)!.ShortName);
        Assert.Equal("HLLV-2", store.GetServer("hllv", 1)!.ShortName);
        Assert.Null(store.GetServer("cs2", 0));
        Assert.Null(store.GetServer("hll", 99));
        Assert.Equal(2, store.GetServers("hllv").Count);
        Assert.Empty(store.GetServers("cs2"));
    }

    [Fact]
    public void OfflineTracking()
    {
        var store = Populated();
        store.MarkOffline("HLL-1");
        Assert.True(store.IsOffline("HLL-1"));
        store.ClearOffline("HLL-1");
        Assert.False(store.IsOffline("HLL-1"));

        store.MarkOffline("HLL-1");
        store.MarkOffline("HLL-2");
        store.ClearAllOffline();
        Assert.False(store.IsOffline("HLL-1"));
        Assert.False(store.IsOffline("HLL-2"));
    }

    [Fact]
    public void PlayerCountTracking()
    {
        var store = Populated();
        Assert.Null(store.GetPlayerCount("HLL-1"));

        store.UpdatePlayerCount("HLL-1", 42, 100);
        Assert.Equal((42, 100), store.GetPlayerCount("HLL-1"));

        store.UpdatePlayerCount("HLL-1", 90, 100); // overwrite
        Assert.Equal((90, 100), store.GetPlayerCount("HLL-1"));

        store.UpdatePlayerCount("HLL-2", 10, 100);
        Assert.Equal((10, 100), store.GetPlayerCount("HLL-2"));
        Assert.Equal((90, 100), store.GetPlayerCount("HLL-1"));
        Assert.Null(store.GetPlayerCount("NOPE"));
    }
}
