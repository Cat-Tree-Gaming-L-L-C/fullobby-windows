using ChllSeeding.Core.Native;

namespace ChllSeeding.Core.Tests;

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
}
