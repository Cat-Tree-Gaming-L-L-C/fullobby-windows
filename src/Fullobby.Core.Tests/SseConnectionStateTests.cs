using Fullobby.Core.Api;

namespace Fullobby.Core.Tests;

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

    // The UI drives its connection indicator off Changed instead of polling these fields once a
    // second, so both halves matter: a real change must notify, and a no-op write must not — the
    // SSE loop reassigns Connected on every reconnect attempt, and waking the UI thread for an
    // unchanged value is the cost this event exists to avoid.

    [Fact]
    public void Changed_FiresOnConnectedTransition()
    {
        var state = new SseConnectionState();
        var fired = 0;
        state.Changed += () => fired++;

        state.Connected = true;
        Assert.Equal(1, fired);

        state.Connected = false;
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Changed_SilentOnRedundantWrite()
    {
        var state = new SseConnectionState();
        state.Connected = true;

        var fired = 0;
        state.Changed += () => fired++;

        state.Connected = true;
        state.Connected = true;
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Changed_TracksFailureCount()
    {
        var state = new SseConnectionState();
        var fired = 0;
        state.Changed += () => fired++;

        state.FailureCount = 3;
        Assert.Equal(1, fired);

        state.FailureCount = 3; // unchanged — no wake
        Assert.Equal(1, fired);

        state.FailureCount = 0;
        Assert.Equal(2, fired);
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
