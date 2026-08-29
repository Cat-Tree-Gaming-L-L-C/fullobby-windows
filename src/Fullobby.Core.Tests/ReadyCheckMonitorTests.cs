using Fullobby.Core.Api;
using Fullobby.Core.Scheduling;

namespace Fullobby.Core.Tests;

/// <summary>The dedupe rule behind the ready-check toast. A check stays open for the full 30
/// minutes before its window and comes back from every poll in that time, so this is what decides
/// between telling the operator once and telling them fifteen times.</summary>
public class ReadyCheckMonitorTests
{
    private static ReadyCheckNotice Check(long id, string server = "PF | 25+") =>
        new() { ReadyCheckId = id, ServerName = server, State = "notified" };

    [Fact]
    public void FirstSighting_IsAnnounced()
    {
        var announced = new HashSet<long>();
        var fresh = ReadyCheckMonitor.SelectFresh(announced, new[] { Check(1) });
        Assert.Single(fresh);
        Assert.Equal(1, fresh[0].ReadyCheckId);
    }

    [Fact]
    public void SameCheckOnEveryPoll_IsAnnouncedOnce()
    {
        var announced = new HashSet<long>();
        var checks = new[] { Check(1) };
        Assert.Single(ReadyCheckMonitor.SelectFresh(announced, checks));
        Assert.Empty(ReadyCheckMonitor.SelectFresh(announced, checks));
        Assert.Empty(ReadyCheckMonitor.SelectFresh(announced, checks));
    }

    [Fact]
    public void ANewCheckAlongsideAnOldOne_AnnouncesOnlyTheNewOne()
    {
        var announced = new HashSet<long>();
        ReadyCheckMonitor.SelectFresh(announced, new[] { Check(1) });

        var fresh = ReadyCheckMonitor.SelectFresh(announced, new[] { Check(1), Check(2, "PF | 50+") });
        Assert.Single(fresh);
        Assert.Equal(2, fresh[0].ReadyCheckId);
        Assert.Equal("PF | 50+", fresh[0].ServerName);
    }

    [Fact]
    public void SettledChecksAreForgotten_SoTheSetTracksWhatIsOutstanding()
    {
        var announced = new HashSet<long>();
        ReadyCheckMonitor.SelectFresh(announced, new[] { Check(1), Check(2) });
        Assert.Equal(2, announced.Count);

        // Both answered — the endpoint stops listing them and the set empties, rather than growing
        // for the lifetime of the process.
        Assert.Empty(ReadyCheckMonitor.SelectFresh(announced, Array.Empty<ReadyCheckNotice>()));
        Assert.Empty(announced);
    }

    [Fact]
    public void OrderIsPreserved_SoTheToastNamesTheSoonestWindowFirst()
    {
        var announced = new HashSet<long>();
        // The API returns soonest-window-first; the toast quotes checks[0].
        var fresh = ReadyCheckMonitor.SelectFresh(
            announced,
            new[] { Check(7, "sooner"), Check(3, "later") });
        Assert.Equal(new[] { "sooner", "later" }, fresh.Select(c => c.ServerName));
    }
}
