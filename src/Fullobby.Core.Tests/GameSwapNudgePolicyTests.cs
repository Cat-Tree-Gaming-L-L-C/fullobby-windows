using Fullobby.Core.Games;
using Fullobby.Core.Seeding;

namespace Fullobby.Core.Tests;

/// <summary>When a player with one game open is nudged to swap to the game that needs seeding.</summary>
public class GameSwapNudgePolicyTests
{
    private const long Minute = 60_000;

    [Fact]
    public void OtherGameNeeded_Nudges()
    {
        var policy = new GameSwapNudgePolicy();
        var plan = policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 0);
        Assert.NotNull(plan);
        Assert.Equal(GameSwapKind.Swap, plan!.Kind);
        Assert.Equal(GameCatalog.Hllv, plan.Target);
    }

    [Fact]
    public void NothingOpen_OrNothingToSeed_OrAlreadyInTheNeededGame_DoesNotNudge()
    {
        var policy = new GameSwapNudgePolicy();
        Assert.Null(policy.Evaluate(GameCatalog.Hllv, [], 0));
        Assert.Null(policy.Evaluate(null, [GameCatalog.Hll], 0));
        Assert.Null(policy.Evaluate(GameCatalog.Hll, [GameCatalog.Hll], 0));
        Assert.Null(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll, GameCatalog.Hllv], 0));
        // None of those started a quiet period.
        Assert.NotNull(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 0));
    }

    [Fact]
    public void QuietForHalfAnHourAfterANudge()
    {
        var policy = new GameSwapNudgePolicy();
        Assert.NotNull(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 0));
        Assert.Null(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 29 * Minute));
        Assert.NotNull(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 30 * Minute));
    }

    [Fact]
    public void NotNow_QuietForTwoHours()
    {
        var policy = new GameSwapNudgePolicy();
        Assert.NotNull(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 0));
        policy.Snooze(1 * Minute);
        Assert.Null(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 60 * Minute));
        Assert.NotNull(policy.Evaluate(GameCatalog.Hllv, [GameCatalog.Hll], 121 * Minute));
    }
}
