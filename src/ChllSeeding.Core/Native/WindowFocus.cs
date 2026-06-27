using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ChllSeeding.Core.Native;

/// <summary>
/// Finds the HLL game window and silently injects splash-bypass keys via PostMessage,
/// with an optional focus-stealing fallback. DI singleton, thread-safe.
/// </summary>
/// <remarks>
/// F13 (0x7C) is a real virtual key UE4 registers as "any button pressed" but that no
/// game binds to anything — safe to spam to dismiss the "press any button" splash.
/// Escape skips the UE4 intro videos (only during the video phase).
/// </remarks>
public sealed class WindowFocus
{
    private const string HllWindowTitle = "Hell Let Loose";
    private const long HwndCacheTtlMs = 1_000;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    private readonly ILogger<WindowFocus> _log;
    private readonly object _cacheGate = new();
    private HWND _cachedHwnd;
    private long _cachedAt;

    public WindowFocus(ILogger<WindowFocus> log) => _log = log;

    /// <summary>Whether the HLL game window currently exists (used by the splash-bypass window wait).</summary>
    public bool HasHllWindow() => !FindHllHwnd().IsNull;

    /// <summary>Invalidate the cached HLL window handle. Call when the game is killed/closed.</summary>
    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cachedHwnd = HWND.Null;
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

    /// <summary>Find the HLL top-level window, caching the handle for 1s and re-validating its
    /// title to avoid stale-handle misdirection. Returns <see cref="HWND.Null"/> if not found.</summary>
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

        HWND result = HWND.Null;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            var title = GetWindowTitle(hwnd);
            if (title is not null && title.Contains(HllWindowTitle, StringComparison.Ordinal))
            {
                result = hwnd;
                return false; // stop
            }
            return true; // continue
        }, default);

        lock (_cacheGate)
        {
            if (result.IsNull || !IsValidHwnd(result))
            {
                _cachedHwnd = HWND.Null;
                return HWND.Null;
            }
            _cachedHwnd = result;
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

    /// <summary>Valid window AND still titled "Hell Let Loose" (guards against the HWND being
    /// recycled for a different process after the game closed).</summary>
    private static bool IsValidHllHwnd(HWND hwnd)
    {
        if (!IsValidHwnd(hwnd))
        {
            return false;
        }
        var title = GetWindowTitle(hwnd);
        return title is not null && title.Contains(HllWindowTitle, StringComparison.Ordinal);
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
