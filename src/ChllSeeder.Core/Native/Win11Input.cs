using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ChllSeeder.Core.Native;

/// <summary>
/// Alternative input paths for Windows 11 locked-screen scenarios, where PostMessage to a
/// background window can be blocked. Port of <c>src-rust/src/backend/win11_input.rs</c>:
/// hardware-level <c>SendInput</c> plus a UI Automation focus attempt. DI singleton.
/// </summary>
public sealed class Win11Input
{
    private readonly ILogger<Win11Input> _log;
    private readonly WindowFocus _windowFocus;

    public Win11Input(ILogger<Win11Input> log, WindowFocus windowFocus)
    {
        _log = log;
        _windowFocus = windowFocus;
    }

    /// <summary>True on Windows 11 (build 22000+) or later. On .NET, <see cref="Environment.OSVersion"/>
    /// reports the true build number, so no GetVersionExW shim is needed.</summary>
    public static bool IsWindows11OrLater()
    {
        var v = Environment.OSVersion.Version;
        return v.Major > 10 || (v.Major == 10 && v.Build >= 22000);
    }

    /// <summary>Send F13 via SendInput (hardware input queue). Returns true if both events posted.</summary>
    public static bool SendF13ViaSendInput() => SendKeyViaSendInput(VIRTUAL_KEY.VK_F13);

    /// <summary>Send Escape via SendInput. Returns true if both events posted.</summary>
    public static bool SendEscapeViaSendInput() => SendKeyViaSendInput(VIRTUAL_KEY.VK_ESCAPE);

    /// <summary>Try all input methods in order of likelihood on Win11-locked: UI Automation focus +
    /// SendInput, then a direct SendInput fallback. Returns true if a method reported success.</summary>
    public bool TryAllInputMethods(bool sendEscape)
    {
        switch (TrySendKeysViaUia(sendEscape))
        {
            case true:
                _log.LogInformation("UI Automation input method succeeded");
                return true;
            case false:
                _log.LogDebug("UI Automation: window not found");
                break;
            case null:
                _log.LogWarning("UI Automation approach failed");
                break;
        }

        if (sendEscape)
        {
            SendEscapeViaSendInput();
            Thread.Sleep(50);
        }

        if (SendF13ViaSendInput())
        {
            _log.LogInformation("Direct SendInput succeeded");
            return true;
        }
        _log.LogWarning("SendInput failed to send keys");
        return false;
    }

    /// <summary>UI Automation focus + SendInput. Returns true on success, false if the window
    /// wasn't found, null if COM/UIA failed.</summary>
    public bool? TrySendKeysViaUia(bool sendEscape)
    {
        var coInit = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        // RPC_E_CHANGED_MODE means COM is already initialized in another mode — still usable.
        var initializedHere = coInit.Succeeded;
        try
        {
            var hr = PInvoke.CoCreateInstance<IUIAutomation>(
                typeof(CUIAutomation).GUID, null!, CLSCTX.CLSCTX_INPROC_SERVER, out var automation);
            if (hr.Failed || automation is null)
            {
                _log.LogWarning("Failed to create UI Automation instance: 0x{Hr:X8}", hr.Value);
                return null;
            }

            var hwnd = _windowFocus.FindHllHwnd();
            if (hwnd.IsNull)
            {
                return false;
            }

            try
            {
                var element = automation.ElementFromHandle(hwnd);
                try
                {
                    element.SetFocus();
                }
                catch (Exception e)
                {
                    _log.LogDebug(e, "UI Automation SetFocus failed");
                }
                Thread.Sleep(50);
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "UI Automation ElementFromHandle failed");
                return null;
            }

            if (sendEscape)
            {
                SendEscapeViaSendInput();
                Thread.Sleep(50);
            }
            return SendF13ViaSendInput();
        }
        finally
        {
            if (initializedHere)
            {
                PInvoke.CoUninitialize();
            }
        }
    }

    private static bool SendKeyViaSendInput(VIRTUAL_KEY key)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki = new KEYBDINPUT { wVk = key };
        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki = new KEYBDINPUT { wVk = key, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP };

        var sent = PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
        return sent == 2;
    }
}
