using ChllSeeding.Core.Seeding;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for the heartbeat backoff math + constants (port of the
/// <c>#[cfg(test)]</c> block in <c>src-rust/src/backend/heartbeat.rs</c>).</summary>
public class HeartbeatTests
{
    [Fact] // test_heartbeat_constants
    public void IntervalIs30()
    {
        Assert.Equal(30, HeartbeatService.HeartbeatIntervalSecs);
        Assert.Equal(90, HeartbeatService.HeartbeatIntervalSecs * 3); // wake threshold
    }

    [Fact]
    public void NoFailures_BaseInterval() => Assert.Equal(30, HeartbeatService.BackoffIntervalSecs(0));

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 300)] // 30*16=480 → capped at 300
    [InlineData(5, 300)] // shift clamps at 4
    [InlineData(20, 300)]
    public void ExponentialBackoffWithCap(int failures, long expected) =>
        Assert.Equal(expected, HeartbeatService.BackoffIntervalSecs(failures));
}
