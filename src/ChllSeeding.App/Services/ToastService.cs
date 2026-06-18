using System.Runtime.InteropServices;
using ChllSeeding.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ChllSeeding.App.Services;

/// <summary>Desktop (Windows) toast notifications + the attention sound played before a server
/// switch. Port of src-rust/src/platform/notification.rs (notify-rust + MessageBeep).
///
/// Lives in the App project because <see cref="AppNotificationManager"/> is a Windows App SDK type;
/// Core stays UI/platform-free and testable.</summary>
public sealed class ToastService
{
    private const uint MB_ICONEXCLAMATION = 0x00000030;

    private readonly ILogger<ToastService> _log;

    public ToastService(ILogger<ToastService> log) => _log = log;

    /// <summary>Raised (off-thread) when the user clicks one of our toasts — the app brings the
    /// window forward so they can snooze/stop.</summary>
    public event Action? Activated;

    /// <summary>Register the unpackaged toast handler. Call once at startup before showing toasts.</summary>
    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _log.LogInformation("Desktop notifications registered");
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Desktop notification registration failed (toasts disabled)");
        }
    }

    /// <summary>Unregister on shutdown (best-effort).</summary>
    public void Unregister()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Desktop notification unregister failed");
        }
    }

    /// <summary>Show the "switching servers" toast and play the attention sound. Mirrors the Rust
    /// flow: play_notification_sound() + show_notification(). Caller has already confirmed
    /// switch_notification is enabled (the engine only emits the switch event when it is).</summary>
    public void ShowServerSwitch(string serverName, long countdownSecs)
    {
        PlayAttentionSound();
        try
        {
            var body = $"Switching servers in {countdownSecs}s. Open {Branding.ProductName} to snooze or stop seeding.";
            var notification = new AppNotificationBuilder()
                .AddText(Branding.ProductName)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            _log.LogInformation("Switch notification shown for {Server}", serverName);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to show switch notification");
        }
    }

    /// <summary>Desktop toast at the start of an auto-seed countdown so the user notices it even when
    /// the window is hidden/in the tray (the in-window overlay alone is invisible then). Port of the
    /// show_notification call at the top of run_autoseed. Deviation: Rust only raised this for NA
    /// (EU passed show_notification=false — an apparent oversight); we show it for both regions.</summary>
    public void ShowAutoseedStarting()
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(Branding.ProductName)
                .AddText("Auto-seed starting in 60 seconds. Open the app to cancel.")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            _log.LogInformation("Auto-seed countdown notification shown");
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to show auto-seed notification");
        }
    }

    /// <summary>Windows "exclamation" chime — exact parity with the Rust MessageBeep call.</summary>
    public void PlayAttentionSound()
    {
        try
        {
            MessageBeep(MB_ICONEXCLAMATION);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "MessageBeep failed");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        => Activated?.Invoke();

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
}
