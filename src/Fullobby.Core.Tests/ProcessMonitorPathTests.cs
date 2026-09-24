using Fullobby.Core.Native;

namespace Fullobby.Core.Tests;

public class ProcessMonitorPathTests
{
    // ── IsHllSteamPath ────────────────────────────────────────────────

    [Fact]
    public void HllSteamPath_Backslash() => Assert.True(ProcessMonitor.IsHllSteamPath(
        @"c:\program files (x86)\steam\steamapps\common\hell let loose\hll-win64-shipping.exe"));

    [Fact]
    public void HllSteamPath_ForwardSlash() => Assert.True(ProcessMonitor.IsHllSteamPath(
        "c:/program files (x86)/steam/steamapps/common/hell let loose/hll-win64-shipping.exe"));

    [Fact]
    public void HllSteamPath_NonSteam() => Assert.False(ProcessMonitor.IsHllSteamPath(
        @"c:\games\hell let loose\hll-win64-shipping.exe"));

    [Fact]
    public void HllSteamPath_WrongGame() => Assert.False(ProcessMonitor.IsHllSteamPath(
        @"c:\steam\steamapps\common\counter-strike\cs.exe"));

    [Fact]
    public void HllSteamPath_Empty() => Assert.False(ProcessMonitor.IsHllSteamPath(""));

    // ── IsGameSteamPath ───────────────────────────────────────────────

    [Fact]
    public void GameSteamPath_Hll() => Assert.True(ProcessMonitor.IsGameSteamPath(
        @"c:\steam\steamapps\common\hell let loose\hll.exe", "hell let loose"));

    [Fact]
    public void GameSteamPath_Hllv() => Assert.True(ProcessMonitor.IsGameSteamPath(
        @"c:\steam\steamapps\common\hllv\hllv.exe", "hllv"));

    [Fact]
    public void GameSteamPath_ForwardSlash() => Assert.True(ProcessMonitor.IsGameSteamPath(
        "c:/steam/steamapps/common/hell let loose/hll.exe", "hell let loose"));

    [Fact]
    public void GameSteamPath_WrongGame() => Assert.False(ProcessMonitor.IsGameSteamPath(
        @"c:\steam\steamapps\common\counter-strike\cs.exe", "hell let loose"));

    [Fact]
    public void GameSteamPath_NonSteam() => Assert.False(ProcessMonitor.IsGameSteamPath(
        @"c:\games\hell let loose\hll.exe", "hell let loose"));

    // The two HLL titles: "hell let loose" is a prefix of "hell let loose - vietnam", so the folder
    // must match as a whole segment or each game would claim the other's processes.

    [Fact]
    public void GameSteamPath_VietnamIsNotHll() => Assert.False(ProcessMonitor.IsGameSteamPath(
        @"d:\steamlibrary\steamapps\common\hell let loose - vietnam\hll\binaries\win64\hll-win64-shipping.exe",
        "hell let loose"));

    [Fact]
    public void GameSteamPath_Vietnam() => Assert.True(ProcessMonitor.IsGameSteamPath(
        @"d:\steamlibrary\steamapps\common\hell let loose - vietnam\launch_hll.exe",
        "hell let loose - vietnam"));

    [Fact]
    public void HllSteamPath_VietnamIsNotHll() => Assert.False(ProcessMonitor.IsHllSteamPath(
        @"c:\steam\steamapps\common\hell let loose - vietnam\launch_hll.exe"));

    [Fact]
    public void GameSteamPath_OtherLibrary() => Assert.True(ProcessMonitor.IsGameSteamPath(
        @"e:\games\steamlibrary\steamapps\common\hell let loose\hll\binaries\win64\hll-win64-shipping.exe",
        "hell let loose"));
}
