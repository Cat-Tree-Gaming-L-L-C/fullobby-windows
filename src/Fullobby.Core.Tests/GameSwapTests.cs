using Fullobby.Core.Games;
using Fullobby.Core.Seeding;

namespace Fullobby.Core.Tests;

/// <summary>What to do about games already open when the directive picks a game to seed.</summary>
public class GameSwapTests
{
    [Fact]
    public void NothingOpen_LaunchesStraightAway()
    {
        var plan = GameSwap.Plan(GameCatalog.Hllv, []);
        Assert.Equal(GameSwapKind.None, plan.Kind);
        Assert.Empty(plan.ToClose);
        Assert.Equal("", plan.PromptNotice);
    }

    [Fact]
    public void OtherGameOpen_IsASwap()
    {
        // HLL started by hand while Vietnam is the game in priority.
        var plan = GameSwap.Plan(GameCatalog.Hllv, [GameCatalog.Hll]);
        Assert.Equal(GameSwapKind.Swap, plan.Kind);
        Assert.Equal([GameCatalog.Hll], plan.ToClose);
        Assert.Equal("Swap games?", plan.Title);
        Assert.Equal(
            "Hell Let Loose is open, but Hell Let Loose: Vietnam is the game that needs seeding right now. " +
            "Close Hell Let Loose and start seeding Hell Let Loose: Vietnam?",
            plan.ConfirmMessage);
        Assert.Equal("Hell Let Loose is open — Seed Now closes it and starts Hell Let Loose: Vietnam.", plan.PromptNotice);
    }

    [Fact]
    public void TargetGameOpenByHand_IsARelaunch()
    {
        // Open, but not on the seeding server: it must close and relaunch onto it.
        var plan = GameSwap.Plan(GameCatalog.Hll, [GameCatalog.Hll]);
        Assert.Equal(GameSwapKind.Relaunch, plan.Kind);
        Assert.Equal([GameCatalog.Hll], plan.ToClose);
        Assert.Equal("Game Running", plan.Title);
        Assert.Equal("Hell Let Loose is currently running. Close the game to continue?", plan.ConfirmMessage);
    }

    [Fact]
    public void BothOpen_ClosesBoth()
    {
        var plan = GameSwap.Plan(GameCatalog.Hllv, [GameCatalog.Hll, GameCatalog.Hllv]);
        Assert.Equal(GameSwapKind.Swap, plan.Kind);
        Assert.Equal([GameCatalog.Hll, GameCatalog.Hllv], plan.ToClose);
        Assert.StartsWith("Hell Let Loose and Hell Let Loose: Vietnam are open", plan.ConfirmMessage);
    }

    [Fact]
    public void UnansweredAutoSeed_SaysWhichGameNeedsSeeding()
    {
        var plan = GameSwap.Plan(GameCatalog.Hllv, [GameCatalog.Hll]);
        Assert.Contains("Hell Let Loose: Vietnam needs seeding", plan.SkippedNotice);
        Assert.Contains("left it alone", plan.SkippedNotice);
    }
}
