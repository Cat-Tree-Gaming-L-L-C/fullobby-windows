using Fullobby.Core.Config;
using Fullobby.Core.Games;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fullobby.Core.Tests;

/// <summary>Tests for the per-game Power Savings preference and the one-time migration
/// from the old global Settings toggle.</summary>
public class EfficiencyPreferenceTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;

    public EfficiencyPreferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fullobby_test_effpref_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Key_IsPerGame()
    {
        Assert.Equal("efficiency_mode.hll", EfficiencyPreference.Key(GameCatalog.Hll));
        Assert.Equal("efficiency_mode.hllv", EfficiencyPreference.Key(GameCatalog.Hllv));
    }

    [Fact]
    public void DefaultsOff_AndRoundTrips()
    {
        Assert.False(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hll));

        EfficiencyPreference.SetEnabled(_config, GameCatalog.Hll, true);
        Assert.True(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hll));

        EfficiencyPreference.SetEnabled(_config, GameCatalog.Hll, false);
        Assert.False(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hll));
    }

    [Fact]
    public void ChoiceIsIndependentPerGame()
    {
        EfficiencyPreference.SetEnabled(_config, GameCatalog.Hll, true);
        Assert.Equal("true", _config.GetString(EfficiencyPreference.Key(GameCatalog.Hll)));
        Assert.Null(_config.GetString(EfficiencyPreference.Key(GameCatalog.Hllv)));
    }

    [Fact]
    public void UnsupportedGame_NeverEnabled()
    {
        // HLLV doesn't support efficiency mode — even a set key reads back as off.
        EfficiencyPreference.SetEnabled(_config, GameCatalog.Hllv, true);
        Assert.False(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hllv));
    }

    // ── Legacy global-toggle migration ────────────────────────────────────

    [Fact]
    public void Migrate_LegacyOn_SeedsSupportedGamesAndDropsKey()
    {
        _config.SetString("efficiency_mode", "true");

        EfficiencyPreference.MigrateLegacyGlobalToggle(_config);

        Assert.True(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hll));
        // Unsupported games are never seeded from the legacy toggle.
        Assert.Null(_config.GetString(EfficiencyPreference.Key(GameCatalog.Hllv)));
        Assert.Null(_config.GetString("efficiency_mode"));
    }

    [Fact]
    public void Migrate_LegacyOff_JustDropsKey()
    {
        _config.SetString("efficiency_mode", "false");

        EfficiencyPreference.MigrateLegacyGlobalToggle(_config);

        Assert.False(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hll));
        Assert.Null(_config.GetString(EfficiencyPreference.Key(GameCatalog.Hll)));
        Assert.Null(_config.GetString("efficiency_mode"));
    }

    [Fact]
    public void Migrate_NeverClobbersAnExistingPerGameChoice()
    {
        EfficiencyPreference.SetEnabled(_config, GameCatalog.Hll, false);
        _config.SetString("efficiency_mode", "true");

        EfficiencyPreference.MigrateLegacyGlobalToggle(_config);

        Assert.False(EfficiencyPreference.IsEnabled(_config, GameCatalog.Hll));
    }

    [Fact]
    public void Migrate_NoLegacyKey_IsANoOp()
    {
        EfficiencyPreference.MigrateLegacyGlobalToggle(_config);

        Assert.Null(_config.GetString(EfficiencyPreference.Key(GameCatalog.Hll)));
        Assert.Null(_config.GetString("efficiency_mode"));
    }
}
