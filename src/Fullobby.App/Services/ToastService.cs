using System.Runtime.InteropServices;
using Fullobby.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Fullobby.App.Services;

/// <summary>Desktop (Windows) toast notifications + the attention sound played before a server
/// switch.
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

    /// <summary>Raised (off-thread) with the button's action when the user clicks a toast button
    /// (see <see cref="ShowWithButtons"/>) instead of the toast body. Not followed by
    /// <see cref="Activated"/>: a "Not now" must not pull the app over the player's game.</summary>
    public event Action<string>? ActionInvoked;

    /// <summary>Toast argument key carrying a button's action.</summary>
    private const string ActionArg = "action";

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

    /// <summary>Show the "switching servers" toast and play the attention sound. Caller has already
    /// confirmed switch_notification is enabled (the engine only emits the switch event when it is).</summary>
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

    /// <summary>Show a plain title/body desktop toast (no sound). Port of the generic
    /// <c>platform::notification::show_notification</c> used by, e.g., the auto-seed countdown.</summary>
    public void Show(string title, string body)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to show notification");
        }
    }

    /// <summary>A title/body toast with buttons, each reporting its action through
    /// <see cref="ActionInvoked"/>. Clicking the body behaves like any other toast.</summary>
    public void ShowWithButtons(string title, string body, IReadOnlyList<(string Label, string Action)> buttons)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);
            foreach (var (label, action) in buttons)
            {
                builder.AddButton(new AppNotificationButton(label).AddArgument(ActionArg, action));
            }
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to show notification");
        }
    }

    /// <summary>Windows "exclamation" chime via MessageBeep.</summary>
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
    {
        if (args.Arguments.TryGetValue(ActionArg, out var action) && action.Length > 0)
        {
            ActionInvoked?.Invoke(action);
            return;
        }
        Activated?.Invoke();
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
}
