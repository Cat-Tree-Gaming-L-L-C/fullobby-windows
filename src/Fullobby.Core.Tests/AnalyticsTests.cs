using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Fullobby.Core.Games;
using Fullobby.Core.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fullobby.Core.Tests;

/// <summary>Tests for the session-start analytics gatherer (port of <c>gather_analytics</c>).</summary>
public class AnalyticsTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;

    public AnalyticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fullobby_test_analytics_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void MissingFlags_DefaultFalse()
    {
        var a = Analytics.Gather(_config, GameCatalog.Hll, autoSeed: false);
        Assert.False(a.EfficiencyMode);
        Assert.False(a.AutoSeed);
    }

    [Fact]
    public void ReadsFlagsFromConfig()
    {
        EfficiencyPreference.SetEnabled(_config, GameCatalog.Hll, true);

        var a = Analytics.Gather(_config, GameCatalog.Hll, autoSeed: true);
        Assert.True(a.EfficiencyMode);
        Assert.True(a.AutoSeed);
    }

    [Fact]
    public void UnsupportedGame_ReportsEfficiencyOff()
    {
        // Even a stray per-game key can't turn efficiency mode on for a game that doesn't support it.
        _config.SetString(EfficiencyPreference.Key(GameCatalog.Hllv), "true");

        var a = Analytics.Gather(_config, GameCatalog.Hllv, autoSeed: false);
        Assert.False(a.EfficiencyMode);
    }

    [Fact]
    public void OsFactsArePopulated()
    {
        var a = Analytics.Gather(_config, GameCatalog.Hll, autoSeed: false);
        Assert.False(string.IsNullOrEmpty(a.OsVersion));
        Assert.False(string.IsNullOrEmpty(a.OsArch));
        // os_version is "unknown" or a dotted version string.
        Assert.True(a.OsVersion == "unknown" || a.OsVersion.Contains('.'));
    }

    [Fact]
    public void OsArchHasKnownShape()
    {
        Assert.Contains(OsInfo.OsArch, new[] { "x86_64", "aarch64", "x86", "arm" });
    }
}
