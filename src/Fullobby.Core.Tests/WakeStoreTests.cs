using Fullobby.Core.Config;
using Fullobby.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fullobby.Core.Tests;

/// <summary>The persisted wake set: round-trip, legacy single-key adoption, and the
/// enabled-state rules the reconciler and watchdog both depend on.</summary>
public sealed class WakeStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;

    public WakeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fullobby_test_wakes_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Empty_WhenNothingStored()
    {
        Assert.Empty(WakeStore.Load(_config));
        Assert.False(WakeStore.IsEnabled(_config));
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        WakeStore.Save(_config,
        [
            new PlannedWake(new TimeOnly(6, 0), [1L, 2L], 4),
            new PlannedWake(new TimeOnly(22, 0), [2L], 6),
        ]);

        var loaded = WakeStore.Load(_config);
        Assert.True(WakeStore.IsEnabled(_config));
        Assert.Equal(2, loaded.Count);
        Assert.Equal("06:00", loaded[0].TimeUtc);
        Assert.Equal([1L, 2L], loaded[0].NetworkIds);
        Assert.Equal(4, loaded[0].MissedWindowHours);
        Assert.Equal("22:00", loaded[1].TimeUtc);
        Assert.False(loaded[0].Legacy);
    }

    [Fact]
    public void UnattributedFleetWake_StoresNullMissedWindow()
    {
        // Null = follow the live fleet config, as the single-slot build did — the value at plan
        // time must not freeze a fleet setting that changes later.
        WakeStore.Save(_config, [new PlannedWake(new TimeOnly(10, 0), [], 4)]);
        Assert.Null(WakeStore.Load(_config)[0].MissedWindowHours);
    }

    [Fact]
    public void LegacyKey_ReadsAsSingleLegacyWake()
    {
        // A pre-wake-set install: auto_seed_time on disk, no wake set. Auto-seed must stay
        // enabled (watchdog included) with the old task name until a reconcile adopts it.
        _config.SetString(ConfigKeys.LegacyAutoSeedTime, "06:30");

        var loaded = WakeStore.Load(_config);
        Assert.True(WakeStore.IsEnabled(_config));
        var wake = Assert.Single(loaded);
        Assert.Equal("06:30", wake.TimeUtc);
        Assert.True(wake.Legacy);
        Assert.Equal("Fullobby", wake.Slot.TaskName);
        Assert.Empty(wake.NetworkIds);
        Assert.Null(wake.MissedWindowHours);
    }

    [Fact]
    public void Save_RetiresTheLegacyKey()
    {
        _config.SetString(ConfigKeys.LegacyAutoSeedTime, "06:30");
        WakeStore.Save(_config, [new PlannedWake(new TimeOnly(6, 30), [], 4)]);

        Assert.Null(_config.GetString(ConfigKeys.LegacyAutoSeedTime));
        var wake = Assert.Single(WakeStore.Load(_config));
        Assert.False(wake.Legacy);
        Assert.Equal("Fullobby-0630", wake.Slot.TaskName);
    }

    [Fact]
    public void Clear_DisablesIncludingLegacy()
    {
        _config.SetString(ConfigKeys.LegacyAutoSeedTime, "06:30");
        WakeStore.Save(_config, [new PlannedWake(new TimeOnly(6, 30), [], 4)]);

        WakeStore.Clear(_config);
        Assert.Empty(WakeStore.Load(_config));
        Assert.False(WakeStore.IsEnabled(_config));
    }

    [Fact]
    public void Load_DropsUnparseableEntries()
    {
        _config.Set(ConfigKeys.AutoSeedWakes, new List<StoredWake>
        {
            new("garbage", [], null),
            new("06:00", [1L], 4),
        });

        var wake = Assert.Single(WakeStore.Load(_config));
        Assert.Equal("06:00", wake.TimeUtc);
    }

    /// <summary>Pinned: both keys are on disk in existing installs. Renaming either silently
    /// disables (or double-arms) auto-seed across an upgrade.</summary>
    [Fact]
    public void StoreKeys_AreStable()
    {
        Assert.Equal("auto_seed_wakes", ConfigKeys.AutoSeedWakes);
        Assert.Equal("auto_seed_time", ConfigKeys.LegacyAutoSeedTime);
    }
}
