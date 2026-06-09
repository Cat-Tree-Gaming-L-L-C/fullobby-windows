using ChllSeeder.Core.Api;

namespace ChllSeeder.Core.Tests;

/// <summary>Tests for the hand-rolled SSE frame parser that replaces the Rust
/// <c>reqwest_eventsource</c> dependency.</summary>
public class SseFrameParserTests
{
    [Fact]
    public void SingleStatsFrame()
    {
        var frames = SseFrameParser.ParseAll("event: stats\ndata: [1,2,3]\n\n").ToList();
        var frame = Assert.Single(frames);
        Assert.Equal("stats", frame.EventType);
        Assert.Equal("[1,2,3]", frame.Data);
    }

    [Fact]
    public void SeedingStatusFrame()
    {
        var frames = SseFrameParser.ParseAll("event: seeding_status\ndata: {\"hll\":{}}\n\n").ToList();
        var frame = Assert.Single(frames);
        Assert.Equal("seeding_status", frame.EventType);
        Assert.Equal("{\"hll\":{}}", frame.Data);
    }

    [Fact]
    public void MultiLineDataIsJoinedWithNewlines()
    {
        var frames = SseFrameParser.ParseAll("event: stats\ndata: line1\ndata: line2\n\n").ToList();
        Assert.Equal("line1\nline2", Assert.Single(frames).Data);
    }

    [Fact]
    public void DefaultEventTypeIsMessage()
    {
        var frames = SseFrameParser.ParseAll("data: hello\n\n").ToList();
        Assert.Equal("message", Assert.Single(frames).EventType);
    }

    [Fact]
    public void CommentAndKeepaliveLinesIgnored()
    {
        var frames = SseFrameParser.ParseAll(":keepalive\n\n:another\n").ToList();
        Assert.Empty(frames);
    }

    [Fact]
    public void ValueWithoutLeadingSpaceIsPreserved()
    {
        // Only a single leading space after the colon is stripped.
        var frames = SseFrameParser.ParseAll("event:stats\ndata:  two-leading\n\n").ToList();
        var frame = Assert.Single(frames);
        Assert.Equal("stats", frame.EventType);
        Assert.Equal(" two-leading", frame.Data);
    }

    [Fact]
    public void MultipleFramesInOneBlock()
    {
        var frames = SseFrameParser.ParseAll(
            "event: stats\ndata: a\n\nevent: heartbeat\ndata: 1\n\nevent: stats\ndata: b\n\n").ToList();
        Assert.Equal(3, frames.Count);
        Assert.Equal("a", frames[0].Data);
        Assert.Equal("heartbeat", frames[1].EventType);
        Assert.Equal("b", frames[2].Data);
    }

    [Fact]
    public void BlankLineWithNoFieldsDispatchesNothing()
    {
        var parser = new SseFrameParser();
        Assert.Null(parser.Feed(""));
        Assert.Null(parser.Feed(""));
    }

    [Fact]
    public void PartialFrameAcrossFeedsDispatchesOnBlankLine()
    {
        var parser = new SseFrameParser();
        Assert.Null(parser.Feed("event: stats"));
        Assert.Null(parser.Feed("data: chunk"));
        var frame = parser.Feed("");
        Assert.NotNull(frame);
        Assert.Equal("stats", frame!.Value.EventType);
        Assert.Equal("chunk", frame.Value.Data);
    }

    [Fact]
    public void CrlfLineEndingsHandled()
    {
        var frames = SseFrameParser.ParseAll("event: stats\r\ndata: x\r\n\r\n").ToList();
        Assert.Equal("x", Assert.Single(frames).Data);
    }
}
