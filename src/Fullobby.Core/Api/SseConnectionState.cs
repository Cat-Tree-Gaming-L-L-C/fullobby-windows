namespace Fullobby.Core.Api;

/// <summary>
/// Shared coordination state between the SSE stream (<see cref="SseStreamClient"/>),
/// the HTTP polling fallback (<c>AppBootstrapper</c>), and the UI. DI singleton, thread-safe.
/// </summary>
public sealed class SseConnectionState
{
    // Edge-triggered wakes (capacity 1): a pending signal while not waiting coalesces to one.
    // Wake-one signaling; a release when already signaled is swallowed.
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private readonly SemaphoreSlim _reconnectWake = new(0, 1);

    private volatile bool _connected;
    private int _failureCount;

    /// <summary>
    /// Raised when <see cref="Connected"/> or <see cref="FailureCount"/> actually changes value.
    /// Exists so the UI can reflect connection state without polling: the shell's countdown timer
    /// used to tick once a second purely to copy these two fields into observable properties, which
    /// kept the UI thread awake 86,400 times a day in an app that mostly sits minimized in the tray.
    /// Fired on whichever thread mutated the state (the SSE loop or the polling fallback), so a UI
    /// subscriber must marshal to its own thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>True while the SSE stream is connected. The polling fallback idles slowly while
    /// this holds and reverts to its aggressive cadence once it clears.</summary>
    public bool Connected
    {
        get => _connected;
        set
        {
            if (_connected == value)
            {
                return;
            }
            _connected = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Consecutive SSE connection failures (for the UI's connection indicator).</summary>
    public int FailureCount
    {
        get => Volatile.Read(ref _failureCount);
        set
        {
            if (Interlocked.Exchange(ref _failureCount, value) != value)
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Wake the polling fallback so it polls now (called when SSE disconnects). Port of
    /// <c>request_poll</c>.</summary>
    public void RequestPoll() => Signal(_pollWake);

    /// <summary>Wait for a poll request or until <paramref name="interval"/> elapses, whichever is
    /// first. Returns true if woken by a poll request, false if the interval elapsed. The caller
    /// polls in either case.</summary>
    public async Task<bool> WaitForPollOrInterval(TimeSpan interval, CancellationToken ct) =>
        await _pollWake.WaitAsync(interval, ct).ConfigureAwait(false);

    /// <summary>Ask the SSE loop to drop its connection and reconnect immediately (e.g. a manual
    /// refresh). Port of <c>request_reconnect</c>.</summary>
    public void RequestReconnect() => Signal(_reconnectWake);

    /// <summary>Wait until a reconnect is requested. For use in the SSE loop's race.</summary>
    public Task WaitForReconnect(CancellationToken ct) => _reconnectWake.WaitAsync(ct);

    private static void Signal(SemaphoreSlim sem)
    {
        try { sem.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }
}
