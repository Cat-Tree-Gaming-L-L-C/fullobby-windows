using ChllSeeding.Core.Api;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for the SSE/poll coordination primitive.</summary>
public class SseConnectionStateTests
{
    [Fact]
    public async Task RequestPoll_WakesWaiterImmediately()
    {
        var state = new SseConnectionState();
        state.RequestPoll(); // pending signal coalesces until awaited

        var woken = await state.WaitForPollOrInterval(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(woken);
    }

    [Fact]
    public async Task WaitForPoll_TimesOutWhenNoRequest()
    {
        var state = new SseConnectionState();
        var woken = await state.WaitForPollOrInterval(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.False(woken);
    }

    [Fact]
    public void ConnectedFlag_RoundTrips()
    {
        var state = new SseConnectionState();
        Assert.False(state.Connected);
        state.Connected = true;
        Assert.True(state.Connected);
        state.Connected = false;
        Assert.False(state.Connected);
    }

    [Fact]
    public void FailureCount_RoundTrips()
    {
        var state = new SseConnectionState { FailureCount = 3 };
        Assert.Equal(3, state.FailureCount);
    }

    [Fact]
    public void RequestPoll_CoalescesMultipleSignals()
    {
        var state = new SseConnectionState();
        // Two requests with no waiter must not throw (capacity-1 semaphore swallows the overflow).
        state.RequestPoll();
        state.RequestPoll();
    }
}
