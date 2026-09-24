using Fullobby.Core.Games;

namespace Fullobby.Core.Tests;

/// <summary>Which games a machine can launch: Steam library folders out of
/// <c>libraryfolders.vdf</c>, and the released games with a manifest in one of them.</summary>
public class InstalledGamesTests
{
    [Fact]
    public void ParseLibraryPaths_ReadsEveryPath_AndUnescapesBackslashes()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"686810"		"123"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            		"apps"
            		{
            			"3079210"		"456"
            		}
            	}
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], InstalledGames.ParseLibraryPaths(vdf));
    }

    [Fact]
    public void ParseLibraryPaths_EmptyWhenNone() =>
        Assert.Empty(InstalledGames.ParseLibraryPaths("\"libraryfolders\" { }"));

    [Fact]
    public void Filter_KeepsCatalogOrder()
    {
        var installed = InstalledGames.Filter(GameCatalog.Released, appId => appId is "3079210" or "686810");
        Assert.Equal(["hll", "hllv"], installed.Select(g => g.Id));
    }

    [Fact]
    public void Filter_VietnamOnly()
    {
        var installed = InstalledGames.Filter(GameCatalog.Released, appId => appId == "3079210");
        Assert.Equal(["hllv"], installed.Select(g => g.Id));
    }

    [Fact]
    public void Get_IsNeverEmpty() => Assert.NotEmpty(InstalledGames.Get());
}
