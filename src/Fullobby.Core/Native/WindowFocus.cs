using Fullobby.Core.Games;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Fullobby.Core.Native;

/// <summary>
/// Finds the seeded game's window and silently injects splash-bypass keys via PostMessage,
/// with an optional focus-stealing fallback. DI singleton, thread-safe.
/// </summary>
/// <remarks>
/// Which game is <see cref="Game"/> (kept in step with the engine's current game). A game with a
/// known <see cref="GameDefinition.WindowTitle"/> is matched by title, as HLL always was; one
/// without (HLL: Vietnam) by an Unreal top-level window owned by its verified game process.
///
/// F13 (0x7C) is a real virtual key UE4 registers as "any button pressed" but that no
/// game binds to anything — safe to spam to dismiss the "press any button" splash.
/// Escape skips the UE4 intro videos (only during the video phase).
/// </remarks>
public sealed class WindowFocus
{
    private const string UnrealWindowClass = "UnrealWindow";
    private const long HwndCacheTtlMs = 1_000;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    private readonly ILogger<WindowFocus> _log;
    private readonly ProcessMonitor _process;
    private readonly object _cacheGate = new();
    private HWND _cachedHwnd;
    private uint _cachedOwnerPid;
    private long _cachedAt;
    private volatile GameDefinition _game = GameCatalog.Hll;

    public WindowFocus(ILogger<WindowFocus> log, ProcessMonitor process)
    {
        _log = log;
        _process = process;
    }

    /// <summary>The game whose window this looks for. Changing it drops the cached handle.</summary>
    public GameDefinition Game
    {
        get => _game;
        set
        {
            if (!ReferenceEquals(_game, value))
            {
                _game = value;
                InvalidateCache();
            }
        }
    }

    /// <summary>Whether the HLL game window currently exists (used by the splash-bypass window wait).</summary>
    public bool HasHllWindow() => !FindHllHwnd().IsNull;

    /// <summary>Invalidate the cached HLL window handle. Call when the game is killed/closed.</summary>
    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cachedHwnd = HWND.Null;
            _cachedOwnerPid = 0;
        }
    }

    /// <summary>Send splash-bypass keys to the HLL window via PostMessage only (no focus stealing).
    /// Sends Escape first when <paramref name="sendEscape"/> (video-skip phase), then F13.
    /// Returns true if the window was found.</summary>
    public bool SendKeysToHll(bool sendEscape)
    {
        var hwnd = FindHllHwnd();
        if (hwnd.IsNull)
        {
            return false;
        }

        if (sendEscape)
        {
            if (!IsValidHllHwnd(hwnd))
            {
                return false;
            }
            PostKey(hwnd, VIRTUAL_KEY.VK_ESCAPE);
        }

        if (!IsValidHllHwnd(hwnd))
        {
            return false;
        }
        PostKey(hwnd, VIRTUAL_KEY.VK_F13);
        return true;
    }

    /// <summary>Force-focus the HLL window using the AttachThreadInput trick (bypasses the Win11
    /// foreground lock), then send F13. This WILL briefly steal focus. Returns true if found.</summary>
    public bool ForceFocusAndSendKey()
    {
        var hwnd = FindHllHwnd();
        if (hwnd.IsNull || !IsValidHllHwnd(hwnd))
        {
            return false;
        }

        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThread = PInvoke.GetWindowThreadProcessId(foreground);
        var ourThread = PInvoke.GetCurrentThreadId();

        var attached = false;
        if (foregroundThread != ourThread && foregroundThread != 0)
        {
            attached = PInvoke.AttachThreadInput(ourThread, foregroundThread, true);
        }

        if (IsValidHllHwnd(hwnd))
        {
            PInvoke.SetForegroundWindow(hwnd);
            PInvoke.SetWindowPos(hwnd, (HWND)default, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }

        if (attached)
        {
            PInvoke.AttachThreadInput(ourThread, foregroundThread, false);
        }

        if (!IsValidHllHwnd(hwnd))
        {
            return false;
        }
        PostKey(hwnd, VIRTUAL_KEY.VK_F13);
        return true;
    }

    /// <summary>Minimize the HLL window (efficiency mode). Returns true if found and minimized.</summary>
    public bool MinimizeHllWindow()
    {
        var hwnd = FindHllHwnd();
        if (hwnd.IsNull || !IsValidHllHwnd(hwnd))
        {
            return false;
        }
        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
        return true;
    }

    /// <summary>Owners of visible Unreal game windows (class <c>UnrealWindow</c>) — any Unreal title,
    /// ours or not. Catches an Unreal game whose exe doesn't follow the shipping naming.</summary>
    public IReadOnlySet<uint> UnrealWindowOwnerPids()
    {
        var pids = new HashSet<uint>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (PInvoke.IsWindowVisible(hwnd) && GetClassName(hwnd) == UnrealWindowClass)
            {
                pids.Add(OwnerPid(hwnd));
            }
            return true; // continue
        }, default);
        return pids;
    }

    /// <summary>Bring the app's own window to the foreground (matched by window title).</summary>
    public void FocusSeedingWindow()
    {
        PInvoke.EnumWindows((hwnd, _) =>
        {
            var title = GetWindowTitle(hwnd);
            if (title is null || !title.Contains(Branding.ProductName, StringComparison.Ordinal))
            {
                return true; // continue
            }
            if (!IsValidHwnd(hwnd))
            {
                return true;
            }
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
            PInvoke.SetForegroundWindow(hwnd);
            PInvoke.SetWindowPos(hwnd, (HWND)default, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            return false; // stop
        }, default);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>Find the game's top-level window, caching the handle for 1s and re-validating it
    /// to avoid stale-handle misdirection. Returns <see cref="HWND.Null"/> if not found.</summary>
    internal HWND FindHllHwnd()
    {
        var now = Environment.TickCount64;

        lock (_cacheGate)
        {
            if (!_cachedHwnd.IsNull
                && now - _cachedAt < HwndCacheTtlMs
                && IsValidHllHwnd(_cachedHwnd))
            {
                return _cachedHwnd;
            }
        }

        var game = _game;
        HWND result = HWND.Null;
        uint resultPid = 0;
        if (game.WindowTitle is { } expected)
        {
            PInvoke.EnumWindows((hwnd, _) =>
            {
                var title = GetWindowTitle(hwnd);
                if (title is not null && title.Contains(expected, StringComparison.Ordinal))
                {
                    result = hwnd;
                    return false; // stop
                }
                return true; // continue
            }, default);
        }
        else
        {
            var pids = _process.GetGamePids(game);
            if (pids.Count > 0)
            {
                PInvoke.EnumWindows((hwnd, _) =>
                {
                    if (!PInvoke.IsWindowVisible(hwnd)
                        || !pids.Contains(OwnerPid(hwnd))
                        || GetClassName(hwnd) != UnrealWindowClass)
                    {
                        return true; // continue
                    }
                    result = hwnd;
                    resultPid = OwnerPid(hwnd);
                    return false; // stop
                }, default);
            }
        }

        lock (_cacheGate)
        {
            if (result.IsNull || !IsValidHwnd(result))
            {
                _cachedHwnd = HWND.Null;
                _cachedOwnerPid = 0;
                return HWND.Null;
            }
            _cachedHwnd = result;
            _cachedOwnerPid = resultPid;
            _cachedAt = now;
            return result;
        }
    }

    private static void PostKey(HWND hwnd, VIRTUAL_KEY key)
    {
        var wParam = (WPARAM)(nuint)(ushort)key;
        PInvoke.PostMessage(hwnd, WM_KEYDOWN, wParam, default);
        Thread.Sleep(50); // 50ms between down/up for reliable registration
        PInvoke.PostMessage(hwnd, WM_KEYUP, wParam, default);
    }

    private static bool IsValidHwnd(HWND hwnd) => !hwnd.IsNull && PInvoke.IsWindow(hwnd);

    /// <summary>Valid window AND still the game's: titled as the game, or owned by the process it was
    /// found under (guards against the HWND being recycled for a different process after the game
    /// closed).</summary>
    private bool IsValidHllHwnd(HWND hwnd)
    {
        if (!IsValidHwnd(hwnd))
        {
            return false;
        }
        if (_game.WindowTitle is { } expected)
        {
            var title = GetWindowTitle(hwnd);
            return title is not null && title.Contains(expected, StringComparison.Ordinal);
        }
        uint owner;
        lock (_cacheGate)
        {
            owner = _cachedOwnerPid;
        }
        return owner != 0 && OwnerPid(hwnd) == owner;
    }

    private static unsafe uint OwnerPid(HWND hwnd)
    {
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        return pid;
    }

    private static string? GetClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        var copied = PInvoke.GetClassName(hwnd, buffer);
        return copied <= 0 ? null : new string(buffer[..copied]);
    }

    private static string? GetWindowTitle(HWND hwnd)
    {
        var len = PInvoke.GetWindowTextLength(hwnd);
        if (len <= 0)
        {
            return null;
        }
        Span<char> buffer = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
        var copied = PInvoke.GetWindowText(hwnd, buffer);
        return copied <= 0 ? null : new string(buffer[..copied]);
    }
}
