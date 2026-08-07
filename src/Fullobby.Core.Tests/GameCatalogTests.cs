using Fullobby.Core.Games;

namespace Fullobby.Core.Tests;

public class GameCatalogTests
{
    [Fact]
    public void ById_Hll()
    {
        var game = GameCatalog.ById("hll");
        Assert.NotNull(game);
        Assert.Equal("hll", game!.Id);
        Assert.Equal("Hell Let Loose", game.DisplayName);
    }

    [Fact]
    public void ById_Hllv()
    {
        var game = GameCatalog.ById("hllv");
        Assert.NotNull(game);
        Assert.Equal("hllv", game!.Id);
        Assert.Equal("HLLV", game.DisplayName);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("HLL")] // lookup is case-sensitive
    public void ById_Unknown_ReturnsNull(string id) =>
        Assert.Null(GameCatalog.ById(id));

    [Fact]
    public void All_CountAndOrder()
    {
        Assert.Equal(2, GameCatalog.All.Count);
        Assert.Equal("hll", GameCatalog.All[0].Id);
        Assert.Equal("hllv", GameCatalog.All[1].Id);
    }

    [Fact]
    public void Released_OnlyHll()
    {
        Assert.Single(GameCatalog.Released);
        Assert.Equal("hll", GameCatalog.Released[0].Id);
    }

    [Fact]
    public void Hll_FieldValues()
    {
        Assert.Equal("686810", GameCatalog.Hll.SteamAppId);
        Assert.Equal("HLL-Win64-Shipping.exe", GameCatalog.Hll.ExeName);
        Assert.Equal("Launch_HLL.exe", GameCatalog.Hll.LauncherExeName);
        Assert.Equal("Hell Let Loose", GameCatalog.Hll.InstallFolder);
        Assert.NotNull(GameCatalog.Hll.ConfigRelativePath);
        Assert.True(GameCatalog.Hll.SupportsEfficiencyMode);
    }

    [Fact]
    public void Hllv_FieldValues()
    {
        Assert.Equal("HLLV", GameCatalog.Hllv.DisplayName);
        Assert.Null(GameCatalog.Hllv.ConfigRelativePath);
        Assert.False(GameCatalog.Hllv.SupportsEfficiencyMode);
    }
}
