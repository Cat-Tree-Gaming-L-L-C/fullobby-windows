using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

public class ScheduledTaskServiceTests
{
    [Fact]
    public void BuildTaskXml_HasWakeAndStartWhenAvailable()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            @"C:\Program Files\Fullobby\Fullobby.exe",
            @"C:\Program Files\Fullobby",
            "--autoseed-na",
            "2026-06-10T12:00:00");

        Assert.Contains("<WakeToRun>true</WakeToRun>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<DaysInterval>1</DaysInterval>", xml);
        Assert.Contains("<StartBoundary>2026-06-10T12:00:00</StartBoundary>", xml);
        Assert.Contains(@"<Command>C:\Program Files\Fullobby\Fullobby.exe</Command>", xml);
        Assert.Contains("<Arguments>--autoseed-na</Arguments>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
    }

    [Fact]
    public void BuildTaskXml_EscapesSpecialChars()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            @"C:\a & b\app.exe", @"C:\a & b", "--autoseed-eu", "2026-06-10T06:00:00");
        Assert.Contains("C:\\a &amp; b\\app.exe", xml);
        Assert.DoesNotContain("a & b\\app", xml); // raw ampersand must not survive
    }

    [Theory]
    [InlineData("Next Run Time: 3/15/2026 9:00:00 AM", "3/15/2026 9:00:00 AM")]
    [InlineData("HostName: PC\nNext Run Time: 3/15/2026 9:00:00 AM\nStatus: Ready", "3/15/2026 9:00:00 AM")]
    public void ParseNextRunTime_ExtractsValue(string output, string expected)
    {
        Assert.Equal(expected, ScheduledTaskService.ParseNextRunTime(output));
    }

    [Fact]
    public void ParseOwnedTaskNames_MatchesExactAndDashPrefixOnly()
    {
        // Realistic /query /fo csv /nh output: our tasks, a near-name from another vendor, and
        // system tasks in folders. Only exact "Fullobby" and "Fullobby-…" root tasks are ours.
        var csv = string.Join("\r\n",
            "\"\\Fullobby\",\"3/15/2026 9:00:00 AM\",\"Ready\"",
            "\"\\Fullobby-0600\",\"3/15/2026 6:00:00 AM\",\"Ready\"",
            "\"\\Fullobby-EU\",\"N/A\",\"Disabled\"",
            "\"\\FullobbyHelper\",\"N/A\",\"Ready\"",
            "\"\\OneDrive Standalone Update Task\",\"N/A\",\"Ready\"",
            "\"\\Microsoft\\Windows\\Fullobby-Fake\",\"N/A\",\"Ready\"",
            "INFO: localized chatter that is not a data row");

        var names = ScheduledTaskService.ParseOwnedTaskNames(csv, "Fullobby");
        Assert.Equal(["Fullobby", "Fullobby-0600", "Fullobby-EU"], names);
    }

    [Fact]
    public void ParseOwnedTaskNames_EmptyOutput_YieldsNothing()
    {
        Assert.Empty(ScheduledTaskService.ParseOwnedTaskNames("", "Fullobby"));
    }

    [Fact]
    public void ParseOwnedTaskNames_DeduplicatesRepeatedRows()
    {
        // /query repeats a task row per trigger.
        var csv = "\"\\Fullobby-0600\",\"a\",\"Ready\"\n\"\\Fullobby-0600\",\"b\",\"Ready\"";
        Assert.Single(ScheduledTaskService.ParseOwnedTaskNames(csv, "Fullobby"));
    }

    [Theory]
    [InlineData("Next Run Time: N/A")]
    [InlineData("Next Run Time:")]
    [InlineData("Status: Ready\nLast Run Time: 1/1/2026")]
    [InlineData("")]
    public void ParseNextRunTime_MissingOrNa_ReturnsNull(string output)
    {
        Assert.Null(ScheduledTaskService.ParseNextRunTime(output));
    }
}
