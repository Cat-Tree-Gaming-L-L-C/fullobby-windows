using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

/// <summary>Wake identity: time-keyed task names / CLI args, and the arg round-trip that tells a
/// scheduled-task launch which wake fired.</summary>
public class AutoSeedSlotTests
{
    [Fact]
    public void ForTime_KeysTaskAndArgByTime()
    {
        var slot = AutoSeedSlot.ForTime(new TimeOnly(6, 0));
        Assert.Equal("Fullobby-0600", slot.TaskName);
        Assert.Equal("--autoseed-0600", slot.CliArg);
    }

    [Fact]
    public void ForTime_ZeroPads()
    {
        var slot = AutoSeedSlot.ForTime(new TimeOnly(3, 5));
        Assert.Equal("Fullobby-0305", slot.TaskName);
        Assert.Equal("--autoseed-0305", slot.CliArg);
    }

    /// <summary>Pinned: the legacy identity is on every pre-wake-set machine (task registered in
    /// Windows, arg baked into it). Changing either strands those installs' tasks.</summary>
    [Fact]
    public void LegacySlot_IsStable()
    {
        Assert.Equal("Fullobby", AutoSeedSlot.Legacy.TaskName);
        Assert.Equal("--autoseed", AutoSeedSlot.Legacy.CliArg);
    }

    [Fact]
    public void WakeArg_RoundTrips()
    {
        var slot = AutoSeedSlot.ForTime(new TimeOnly(22, 30));
        Assert.True(AutoSeedSlot.TryParseWakeArg(slot.CliArg, out var t));
        Assert.Equal(new TimeOnly(22, 30), t);
    }

    [Theory]
    [InlineData("--autoseed")]      // wake-less legacy form
    [InlineData("--autoseed-na")]   // legacy region forms — autoseed args, but not wake-keyed
    [InlineData("--autoseed-eu")]
    [InlineData("--autoseed-2460")] // out-of-range minute
    [InlineData("--autoseed-9900")] // out-of-range hour
    [InlineData("--autoseed-060")]  // wrong length
    [InlineData("--autoseed-06000")]
    [InlineData("--autoseed-06:0")] // non-digits
    [InlineData(null)]
    public void WakeArg_RejectsNonWakeForms(string? arg)
    {
        Assert.False(AutoSeedSlot.TryParseWakeArg(arg, out _));
    }

    [Theory]
    [InlineData("--autoseed")]
    [InlineData("--autoseed-0600")]
    [InlineData("--autoseed-na")]
    [InlineData("--autoseed-eu")]
    [InlineData("--seed-na")]
    [InlineData("--seed-eu")]
    public void IsAutoseedArg_AcceptsAllLaunchForms(string arg)
    {
        Assert.True(AutoSeedSlot.IsAutoseedArg(arg));
    }

    [Theory]
    [InlineData("--seed")]
    [InlineData("--autoseed-999")]
    [InlineData("autoseed-0600")]
    [InlineData("")]
    public void IsAutoseedArg_RejectsEverythingElse(string arg)
    {
        Assert.False(AutoSeedSlot.IsAutoseedArg(arg));
    }
}
