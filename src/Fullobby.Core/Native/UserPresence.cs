using System.Runtime.InteropServices;

namespace Fullobby.Core.Native;

/// <summary>
/// Whether the user can be offered something right now, as Windows sees it
/// (<c>SHQueryUserNotificationState</c>): not in a fullscreen game or app, not presenting, not in
/// Focus Assist's quiet hours, and not away from an unlocked desktop. This catches games we don't
/// detect ourselves, which is what keeps optional notices (the seed nudge) off a player who is
/// gaming in something other than an Unreal Engine title.
/// </summary>
public static class UserPresence
{
    // QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS
    private const int AcceptsNotificationsState = 5;

    /// <summary>True only when Windows says notifications are welcome. Anything else — busy,
    /// fullscreen, presentation, quiet time, locked — or a failed query counts as no: an optional
    /// notice is never worth the risk of landing on someone mid-game.</summary>
    public static bool AcceptsNotifications()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && state == AcceptsNotificationsState;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
