using ChllSeeding.Core.Api;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for the SSE reconnect backoff + wake-from-sleep math.</summary>
public class SseBackoffTests
{
    [Fact]
    public void ZeroFailures_NoBackoff() => Assert.Equal(0, SseStreamClient.ComputeBackoffSecs(0));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 64)]
    [InlineData(8, 128)]
    [InlineData(9, 256)]
    public void ExponentialDoubling(int failures, long expected) =>
        Assert.Equal(expected, SseStreamClient.ComputeBackoffSecs(failures));

    [Theory]
    [InlineData(10)] // 512 → capped
    [InlineData(20)]
    [InlineData(100)] // would overflow a naive 1<<failures shift
    public void CapsAt300(int failures) =>
        Assert.Equal(300, SseStreamClient.ComputeBackoffSecs(failures));

    [Fact]
    public void IsWakeFromSleep_AboveThreshold() =>
        Assert.True(SseStreamClient.IsWakeFromSleep(TimeSpan.FromSeconds(121), 120));

    [Fact]
    public void IsWakeFromSleep_AtOrBelowThreshold()
    {
        Assert.False(SseStreamClient.IsWakeFromSleep(TimeSpan.FromSeconds(120), 120));
        Assert.False(SseStreamClient.IsWakeFromSleep(TimeSpan.FromSeconds(60), 120));
    }
}
