using ChllSeeding.Core.Seeding;

namespace ChllSeeding.Core.Tests;

/// <summary>Port of the <c>compute_stagger_secs</c> + timing-constant tests in
/// <c>backend/seeding.rs</c>. The pure <see cref="SeedingEngine.ComputeStaggerSecs"/> takes the
/// player-count tuple directly (instead of reading the global server store).</summary>
public class SeedingStaggerTests
{
    [Fact] // test_compute_stagger_no_data
    public void NoData_MidRangeDefault() =>
        Assert.Equal(SeedingEngine.StaggerMaxSecs / 2, SeedingEngine.ComputeStaggerSecs(50, null));

    [Fact] // test_compute_stagger_at_threshold
    public void AtThreshold_MaxStagger() =>
        Assert.Equal(SeedingEngine.StaggerMaxSecs, SeedingEngine.ComputeStaggerSecs(50, (50, 100)));

    [Fact] // test_compute_stagger_full_server
    public void FullServer_NoStagger() =>
        Assert.Equal(0, SeedingEngine.ComputeStaggerSecs(50, (100, 100)));

    [Fact] // test_compute_stagger_half_full
    public void HalfFull_HalfStagger() =>
        // fill_ratio = 25/50 = 0.5 → 90 * 0.5 = 45
        Assert.Equal(SeedingEngine.StaggerMaxSecs / 2, SeedingEngine.ComputeStaggerSecs(50, (75, 100)));

    [Fact] // test_compute_stagger_nearly_full
    public void NearlyFull_ShortStagger()
    {
        // fill_ratio = 40/50 = 0.8 → 90 * 0.2 ≈ 18 (float truncation may vary by 1)
        var stagger = SeedingEngine.ComputeStaggerSecs(50, (90, 100));
        Assert.InRange(stagger, 16, 18);
    }

    [Fact] // test_compute_stagger_below_threshold
    public void BelowThreshold_MaxStagger() =>
        Assert.Equal(SeedingEngine.StaggerMaxSecs, SeedingEngine.ComputeStaggerSecs(50, (30, 100)));

    [Fact] // test_timing_constants_sanity
    public void TimingConstantsSanity()
    {
        Assert.True(SeedingEngine.SplashBypassMinSecs < SeedingEngine.SplashBypassMaxSecs);
        Assert.True(SeedingEngine.SplashBypassMinSecs > 0);
        Assert.True(SeedingEngine.MonitorMinIntervalSecs < SeedingEngine.MonitorMaxIntervalSecs);
        Assert.True(SeedingEngine.MonitorBackoffStepSecs > 0);
    }
}
