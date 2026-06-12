using ChllSeeding.Core.Api;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for the session-start analytics gatherer (port of <c>gather_analytics</c>).</summary>
public class AnalyticsTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;

    public AnalyticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "chll_test_analytics_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void MissingFlags_DefaultFalse()
    {
        var a = Analytics.Gather(_config, autoSeed: false);
        Assert.False(a.EfficiencyMode);
        Assert.False(a.EuEnabled);
        Assert.False(a.AutoSeed);
    }

    [Fact]
    public void ReadsFlagsFromConfig()
    {
        _config.SetString("efficiency_mode", "true");
        _config.SetString("eu_enabled", "true");

        var a = Analytics.Gather(_config, autoSeed: true);
        Assert.True(a.EfficiencyMode);
        Assert.True(a.EuEnabled);
        Assert.True(a.AutoSeed);
    }

    [Fact]
    public void OsFactsArePopulated()
    {
        var a = Analytics.Gather(_config, autoSeed: false);
        Assert.False(string.IsNullOrEmpty(a.OsVersion));
        Assert.False(string.IsNullOrEmpty(a.OsArch));
        // os_version is "unknown" or a dotted version string (matches the Rust contract).
        Assert.True(a.OsVersion == "unknown" || a.OsVersion.Contains('.'));
    }

    [Fact]
    public void OsArchHasKnownShape()
    {
        Assert.Contains(OsInfo.OsArch, new[] { "x86_64", "aarch64", "x86", "arm" });
    }
}
