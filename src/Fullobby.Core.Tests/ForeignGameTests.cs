using Fullobby.Core.Games;
using Fullobby.Core.Seeding;

namespace Fullobby.Core.Tests;

/// <summary>Other Unreal Engine games: recognising them, naming them, and what a seed does about them.</summary>
public class ForeignGameTests
{
    private static readonly ForeignGame Wardogs = new(4242, "Wardogs-Win64-Shipping.exe", "Wardogs");

    [Theory]
    [InlineData("Wardogs-Win64-Shipping.exe", true)]
    [InlineData("FortniteClient-Win64-Shipping.exe", true)]
    [InlineData("SomeGame-WinGDK-Shipping.exe", true)]      // Game Pass / Xbox PC build
    [InlineData("hll-win64-shipping.exe", true)]            // case-insensitive
    [InlineData("-Win64-Shipping.exe", false)]              // no project name
    [InlineData("Launch_HLL.exe", false)]
    [InlineData("steam.exe", false)]
    [InlineData("Win64-Shipping.exe.bak", false)]
    public void IsUnrealShippingExe(string exe, bool expected) =>
        Assert.Equal(expected, ForeignGame.IsUnrealShippingExe(exe));

    [Theory]
    [InlineData("Wardogs-Win64-Shipping.exe", "Wardogs")]
    [InlineData("SomeGame-WinGDK-Shipping.exe", "SomeGame")]
    [InlineData("RenamedGame.exe", "RenamedGame")]
    public void NameFromExe(string exe, string expected) =>
        Assert.Equal(expected, ForeignGame.NameFromExe(exe));

    [Fact]
    public void AnotherUnrealGameOpen_IsASwap_WithContinueAnyway()
    {
        var plan = GameSwap.Plan(GameCatalog.Hll, [], [Wardogs]);
        Assert.Equal(GameSwapKind.Swap, plan.Kind);
        Assert.True(plan.HasForeign);
        Assert.True(plan.AllowContinueAnyway);
        Assert.Equal("Close Wardogs?", plan.Title);
        Assert.Equal(
            "Wardogs is open. Unreal Engine games like Hell Let Loose usually fail to launch while another one " +
            "is running. Close Wardogs and start seeding Hell Let Loose?",
            plan.ConfirmMessage);
        Assert.Equal("Close and seed", plan.NudgeAcceptLabel);
    }

    [Fact]
    public void OurGameAndAnotherUnrealGame_CloseBoth_NoContinueAnyway()
    {
        // Our own game genuinely has to close for the launch to land on the server.
        var plan = GameSwap.Plan(GameCatalog.Hllv, [GameCatalog.Hll], [Wardogs]);
        Assert.Equal(GameSwapKind.Swap, plan.Kind);
        Assert.False(plan.AllowContinueAnyway);
        Assert.Equal("Hell Let Loose and Wardogs", plan.ClosingNames);
        Assert.StartsWith("Hell Let Loose and Wardogs are open.", plan.ConfirmMessage);
    }

    [Fact]
    public void TargetOpenPlusAnotherUnrealGame_IsNotARelaunch()
    {
        var plan = GameSwap.Plan(GameCatalog.Hll, [GameCatalog.Hll], [Wardogs]);
        Assert.Equal(GameSwapKind.Swap, plan.Kind);
    }

    [Fact]
    public void Consent_CoversOnlyWhatWasShown()
    {
        var shown = GameSwap.Plan(GameCatalog.Hll, [], [Wardogs]);

        // Same thing open (a new pid for the same exe is still what they agreed to close).
        var same = GameSwap.Plan(GameCatalog.Hll, [], [Wardogs with { Pid = 5151 }]);
        Assert.True(same.IsCoveredBy(shown, sameTarget: true));

        // Something opened since.
        var more = GameSwap.Plan(GameCatalog.Hll, [], [Wardogs, new ForeignGame(7, "Other-Win64-Shipping.exe", "Other")]);
        Assert.False(more.IsCoveredBy(shown, sameTarget: true));

        // Less is fine.
        Assert.True(GameSwap.Plan(GameCatalog.Hll, []).IsCoveredBy(shown, sameTarget: true));

        // A different target: fine for an answered auto-seed prompt, not for the nudge.
        var otherTarget = GameSwap.Plan(GameCatalog.Hllv, [], [Wardogs]);
        Assert.True(otherTarget.IsCoveredBy(shown, sameTarget: false));
        Assert.False(otherTarget.IsCoveredBy(shown, sameTarget: true));
    }

    [Fact]
    public void Nudge_ForAnotherUnrealGame_EvenWithOneGameOfOurs()
    {
        var policy = new GameSwapNudgePolicy();
        var plan = policy.Evaluate(GameCatalog.Hll, [], 0, [Wardogs]);
        Assert.NotNull(plan);
        Assert.True(plan!.HasForeign);
        Assert.Contains("usually won't launch while Wardogs is open", plan.NudgeMessage);
    }

    [Fact]
    public void Nudge_NotWhileInTheGameThatNeedsSeeding()
    {
        // Already playing HLL with Wardogs in the background: HLL is fine where it is.
        Assert.False(GameSwapNudgePolicy.Wanted(GameCatalog.Hll, [GameCatalog.Hll], [Wardogs]));
    }
}
