using Fullobby.Core.Api;
using Fullobby.Core.Games;

namespace Fullobby.Core.Tests;

/// <summary><see cref="GameAvailability"/> is process-wide state: every test class that reads or
/// writes it (directly, or through <c>GameCatalog.Released</c>/<c>InstalledGames</c>) runs in this
/// collection, one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameAvailabilityCollection
{
    public const string Name = "GameAvailability";
}

[Collection(GameAvailabilityCollection.Name)]
public sealed class GameAvailabilityTests : IDisposable
{
    public GameAvailabilityTests() => GameAvailability.Reset();

    public void Dispose() => GameAvailability.Reset();

    [Fact]
    public void TheRegistry_OffersTheSeedingGamesThisClientKnows()
    {
        GameAvailability.SetRegistry([
            ("hll", true, true),
            ("wardogs", true, true),      // seeds, but this client can't launch it
            ("squad", true, false),
            ("palworld", false, false),   // doesn't seed
        ]);
        Assert.Equal(["hll", "squad"], GameAvailability.Offered.Select(g => g.Id));
        Assert.True(GameAvailability.IsUntested("squad"));
        Assert.False(GameAvailability.IsUntested("hll"));
    }

    [Fact]
    public void AnEmptyRegistry_StillOffersHll()
    {
        GameAvailability.SetRegistry([("palworld", false, false)]);
        Assert.Equal(["hll"], GameAvailability.Offered.Select(g => g.Id));
    }

    [Fact]
    public void WithoutChoices_NewGamesAreOut()
    {
        GameAvailability.SetRegistry([("hll", true, true), ("hllv", true, true), ("squad", true, false)]);
        Assert.Equal(["hll", "hllv"], GameAvailability.OptedIn.Select(g => g.Id));

        GameAvailability.SetChoices(new Dictionary<string, bool> { ["squad"] = true, ["hll"] = false });
        Assert.Equal(["hllv", "squad"], GameAvailability.OptedIn.Select(g => g.Id));

        // Signed out: the defaults again.
        GameAvailability.SetChoices(null);
        Assert.Equal(["hll", "hllv"], GameAvailability.OptedIn.Select(g => g.Id));
    }

    [Fact]
    public void ChoicesChange_ReachesInstalledGamesAtOnce()
    {
        var raised = 0;
        void OnChanged() => raised++;
        GameAvailability.Changed += OnChanged;
        try
        {
            GameAvailability.SetChoices(new Dictionary<string, bool> { ["hll"] = true });
            Assert.Equal(1, raised);
        }
        finally
        {
            GameAvailability.Changed -= OnChanged;
        }
    }

    [Fact]
    public void TheStore_RoundTrips_AndSkipsJunk()
    {
        var reg = GameAvailabilityStore.ParseRegistry("hll:1:1,squad:1:0,bad,:1:1,palworld:0:0");
        Assert.Equal([("hll", true, true), ("squad", true, false), ("palworld", false, false)], reg);

        var choices = GameAvailabilityStore.ParseChoices("squad:1,hll:0,junk");
        Assert.Equal(new Dictionary<string, bool> { ["squad"] = true, ["hll"] = false }, choices);
        Assert.Null(GameAvailabilityStore.ParseChoices(""));
        Assert.Null(GameAvailabilityStore.ParseChoices(null));
    }

    [Fact]
    public void AServer_IsJoinedByHand_WhenTheApiOrTheCatalogSaysSo()
    {
        var api = new ServerInfo { Ip = "1.2.3.4:7787", Launch = "manual" };
        Assert.True(api.IsManualJoin(GameCatalog.Hll));
        // An API older than the registry sends no launch: the catalog decides.
        var old = new ServerInfo { Ip = "1.2.3.4:7787" };
        Assert.True(old.IsManualJoin(GameCatalog.Squad));
        Assert.False(old.IsManualJoin(GameCatalog.Rust));
        Assert.False(new ServerInfo { Launch = "client" }.IsManualJoin(GameCatalog.Squad));
        // The join code wins over the address where there is one.
        Assert.Equal("ABCD-1234", new ServerInfo { Ip = "1.2.3.4", JoinId = "ABCD-1234" }.JoinLine);
        Assert.Equal("1.2.3.4:7787", api.JoinLine);
    }
}
