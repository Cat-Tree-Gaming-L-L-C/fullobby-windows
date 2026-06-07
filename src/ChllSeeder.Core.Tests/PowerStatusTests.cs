using ChllSeeder.Core.Native;

namespace ChllSeeder.Core.Tests;

public class PowerStatusTests
{
    // ── ParseModernStandbyOutput ──────────────────────────────────────

    [Fact]
    public void ModernStandby_Present()
    {
        var output = "The following sleep states are available on this system:\r\n" +
                     "Standby (S0 Low Power Idle) Network Connected\r\n" +
                     "Hibernate\r\n";
        Assert.True(PowerStatus.ParseModernStandbyOutput(output));
    }

    [Fact]
    public void ModernStandby_Absent()
    {
        var output = "The following sleep states are available on this system:\r\n" +
                     "Standby (S3)\r\n" +
                     "Hibernate\r\n";
        Assert.False(PowerStatus.ParseModernStandbyOutput(output));
    }

    [Fact]
    public void ModernStandby_Empty() => Assert.False(PowerStatus.ParseModernStandbyOutput(""));

    // ── ParseWakeTimersOutput ─────────────────────────────────────────

    [Fact]
    public void WakeTimers_Enabled()
    {
        var output = "Power Setting GUID: bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d  (Allow wake timers)\r\n" +
                     "Current AC Power Setting Index: 0x00000001\r\n" +
                     "Current DC Power Setting Index: 0x00000002\r\n";
        Assert.True(PowerStatus.ParseWakeTimersOutput(output));
    }

    [Fact]
    public void WakeTimers_Disabled()
    {
        var output = "Power Setting GUID: bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d  (Allow wake timers)\r\n" +
                     "Current AC Power Setting Index: 0x00000000\r\n" +
                     "Current DC Power Setting Index: 0x00000000\r\n";
        Assert.False(PowerStatus.ParseWakeTimersOutput(output));
    }

    [Fact]
    public void WakeTimers_Empty() => Assert.False(PowerStatus.ParseWakeTimersOutput(""));

    // ── ComposePowerWarnings ──────────────────────────────────────────

    [Fact]
    public void Warnings_NoIssues() => Assert.Null(PowerStatus.ComposePowerWarnings(false, true));

    [Fact]
    public void Warnings_WakeTimersDisabled()
    {
        var result = PowerStatus.ComposePowerWarnings(false, false);
        Assert.NotNull(result);
        Assert.Contains("Allow wake timers", result);
        Assert.DoesNotContain("Modern Standby", result);
    }

    [Fact]
    public void Warnings_ModernStandby()
    {
        var result = PowerStatus.ComposePowerWarnings(true, true);
        Assert.NotNull(result);
        Assert.Contains("Modern Standby", result);
        Assert.DoesNotContain("Allow wake timers", result);
    }

    [Fact]
    public void Warnings_BothIssues()
    {
        var result = PowerStatus.ComposePowerWarnings(true, false);
        Assert.NotNull(result);
        Assert.Contains("Allow wake timers", result);
        Assert.Contains("Modern Standby", result);
    }
}
