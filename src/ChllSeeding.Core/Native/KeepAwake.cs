using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace ChllSeeding.Core.Native;

/// <summary>
/// Prevents the system (and display) from sleeping while seeding is active, so an
/// unattended seed isn't cut short by power management. This is a <b>new</b> feature,
/// implemented via <c>SetThreadExecutionState</c>.
/// </summary>
/// <remarks>
/// The execution-state request is owned by the calling thread and is released when that
/// thread exits, so we hold it on a dedicated long-lived worker thread that re-asserts
/// periodically (cheap, and robust against any transient clears). DI singleton, thread-safe.
/// </remarks>
public sealed class KeepAwake : IDisposable
{
    private const EXECUTION_STATE SeedingState =
        EXECUTION_STATE.ES_CONTINUOUS
        | EXECUTION_STATE.ES_SYSTEM_REQUIRED
        | EXECUTION_STATE.ES_DISPLAY_REQUIRED;

    private static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<KeepAwake> _log;
    private readonly object _gate = new();

    private Thread? _worker;
    private ManualResetEventSlim? _stopSignal;
    private bool _disposed;

    public KeepAwake(ILogger<KeepAwake> log) => _log = log;

    /// <summary>Whether a keep-awake request is currently held.</summary>
    public bool IsActive
    {
        get { lock (_gate) { return _worker is not null; } }
    }

    /// <summary>Begin keeping the system and display awake. Idempotent — a second call while
    /// already active is a no-op.</summary>
    public void Acquire()
    {
        lock (_gate)
        {
            if (_disposed || _worker is not null)
            {
                return;
            }

            var stop = new ManualResetEventSlim(false);
            var worker = new Thread(() => HoldLoop(stop))
            {
                IsBackground = true,
                Name = "chll-keep-awake",
            };
            _stopSignal = stop;
            _worker = worker;
            worker.Start();
            _log.LogInformation("Keep-awake acquired (system + display)");
        }
    }

    /// <summary>Release the keep-awake request and let normal power management resume.
    /// Idempotent — a no-op when not active.</summary>
    public void Release()
    {
        Thread? worker;
        ManualResetEventSlim? stop;
        lock (_gate)
        {
            worker = _worker;
            stop = _stopSignal;
            _worker = null;
            _stopSignal = null;
        }

        if (worker is null)
        {
            return;
        }

        stop!.Set();
        worker.Join();
        stop.Dispose();
        _log.LogInformation("Keep-awake released");
    }

    private void HoldLoop(ManualResetEventSlim stop)
    {
        // Assert the request on this thread, re-asserting until released. Clearing back to
        // ES_CONTINUOUS on exit lets the system resume normal idle behavior.
        do
        {
            if (PInvoke.SetThreadExecutionState(SeedingState) == 0)
            {
                _log.LogWarning("SetThreadExecutionState failed to assert keep-awake");
            }
        }
        while (!stop.Wait(ReassertInterval));

        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        Release();
    }
}
