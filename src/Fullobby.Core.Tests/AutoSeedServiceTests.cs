using Fullobby.Core.Api;
using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

/// <summary>The pure wake-time derivation used by auto-seed setup and the self-rescheduling
/// resleep path: the earliest active-window start (UTC), or the daily reset hour when no
/// windows are configured.</summary>
public class AutoSeedServiceTests
{
    [Fact]
    public void WakeTimeUtc_NoWindows_UsesDailyResetHour()
    {
        var cfg = new SeedingConfig { ActiveWindows = [], DailyResetHourUtc = 10 };
        Assert.Equal((10, 0), AutoSeedService.WakeTimeUtc(cfg));
    }

    [Fact]
    public void WakeTimeUtc_UsesEarliestWindowStart()
    {
        var cfg = new SeedingConfig
        {
            // 20:00–22:00 and 12:15–14:00 → earliest start is 12:15.
            ActiveWindows =
            [
                new TimeWindow { StartMin = 1200, EndMin = 1320 },
                new TimeWindow { StartMin = 735, EndMin = 840 },
            ],
        };
        Assert.Equal((12, 15), AutoSeedService.WakeTimeUtc(cfg));
    }

    [Fact]
    public void WakeTimeUtc_ClampsResetHour()
    {
        var cfg = new SeedingConfig { ActiveWindows = [], DailyResetHourUtc = 99 };
        Assert.Equal((23, 0), AutoSeedService.WakeTimeUtc(cfg));
    }
}
