using Fullobby.Core.Api;
using Fullobby.Core.Seeding;

namespace Fullobby.Core.Tests;

public class SeedTargetsTests
{
    private static SeedingCandidate Cand(string game, long dbId) => new() { Game = game, DbId = dbId };

    private static NetworkSeedingStatus Net(long id, bool active, SeedingCandidate? hll = null, SeedingCandidate? hllv = null) =>
        new() { NetworkId = id, Active = active, Hll = hll, Hllv = hllv };

    private static readonly IReadOnlySet<long> Member = new HashSet<long> { 1 };

    [Fact]
    public void OnlyTheUsersActiveNetworks_AndInstalledGames()
    {
        var status = new SeedingStatusResponse
        {
            Networks =
            [
                Net(1, active: true, hll: Cand("hll", 10), hllv: Cand("hllv", 11)),
                Net(2, active: true, hll: Cand("hll", 20)),   // not the user's network
                Net(1, active: false, hll: Cand("hll", 30)),  // closed for now
            ],
        };

        Assert.Equal([10L, 11L], SeedTargets.For(status, Member, ["hll", "hllv"]).Select(c => c.DbId));
        Assert.Equal([11L], SeedTargets.For(status, Member, ["hllv"]).Select(c => c.DbId));
        Assert.Empty(SeedTargets.For(status, new HashSet<long>(), ["hll", "hllv"]));
    }

    [Fact]
    public void Added_IsWhatsNewSinceTheLastPush()
    {
        IReadOnlyList<SeedingCandidate> now = [Cand("hll", 10), Cand("hllv", 11)];
        Assert.Equal(2, SeedTargets.Added(new HashSet<long>(), now).Count);
        Assert.Equal([11L], SeedTargets.Added(new HashSet<long> { 10 }, now).Select(c => c.DbId));
        Assert.Empty(SeedTargets.Added(new HashSet<long> { 10, 11 }, now));
    }

    [Fact]
    public void SoleGame_OnlyWhenTheTargetsAgree()
    {
        Assert.Equal("hllv", SeedTargets.SoleGame([Cand("hllv", 1), Cand("hllv", 2)]));
        Assert.Null(SeedTargets.SoleGame([Cand("hll", 1), Cand("hllv", 2)]));
        Assert.Null(SeedTargets.SoleGame([]));
    }
}
