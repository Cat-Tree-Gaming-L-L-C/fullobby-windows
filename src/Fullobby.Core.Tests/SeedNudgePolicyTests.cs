using Fullobby.Core.Games;
using Fullobby.Core.Seeding;

namespace Fullobby.Core.Tests;

/// <summary>When a player who is free (not gaming) is nudged that a game needs seeding.</summary>
public class SeedNudgePolicyTests
{
    private const long Minute = 60_000;

    [Fact]
    public void SomethingToSeed_PlayerFree_Nudges() =>
        Assert.True(new SeedNudgePolicy().ShouldNudge(GameCatalog.Hllv, playerBusy: false, 0));

    [Fact]
    public void NothingToSeed_OrPlayerBusy_DoesNotNudge()
    {
        var policy = new SeedNudgePolicy();
        Assert.False(policy.ShouldNudge(null, playerBusy: false, 0));
        Assert.False(policy.ShouldNudge(GameCatalog.Hll, playerBusy: true, 0));
        // Neither started a quiet period.
        Assert.True(policy.ShouldNudge(GameCatalog.Hll, playerBusy: false, 0));
    }

    [Fact]
    public void QuietForHalfAnHourAfterANudge()
    {
        var policy = new SeedNudgePolicy();
        Assert.True(policy.ShouldNudge(GameCatalog.Hll, false, 0));
        Assert.False(policy.ShouldNudge(GameCatalog.Hll, false, 29 * Minute));
        Assert.True(policy.ShouldNudge(GameCatalog.Hll, false, 30 * Minute));
    }

    [Fact]
    public void NotNow_QuietForTwoHours()
    {
        var policy = new SeedNudgePolicy();
        Assert.True(policy.ShouldNudge(GameCatalog.Hll, false, 0));
        policy.Snooze(1 * Minute);
        Assert.False(policy.ShouldNudge(GameCatalog.Hll, false, 60 * Minute));
        Assert.True(policy.ShouldNudge(GameCatalog.Hll, false, 121 * Minute));
    }
}
