using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Security;

/// <summary>
/// Restricts a directory's ACL to the current user. Used for every directory that holds
/// credential-bearing data: the config store (tokens, API key) and the WebView2 profile
/// (admin-panel session cookies).
/// </summary>
public static class DirectoryHardening
{
    /// <summary>
    /// Disable ACL inheritance and grant the owning user full control.
    ///
    /// Built in-process rather than by shelling out to <c>icacls</c>. Two reasons: two process
    /// creations plus a blocking wait is far too much for a path that runs during startup, and the
    /// shell-out order was unsafe — inheritance was stripped first, so a failed grant afterwards
    /// (domain-qualified or non-ASCII username, non-zero exit) left the directory with an empty
    /// DACL and no explicit grant at all. Here the protection flag and the grant are applied in a
    /// single <c>SetAccessControl</c>, so there is no window in which the directory has no grant,
    /// and the SID comes from the process token instead of a string needing sanitisation.
    ///
    /// Non-fatal: on failure the directory keeps whatever ACL it had. DPAPI remains the primary
    /// control for the config store.
    /// </summary>
    public static void RestrictToCurrentUser(string path, ILogger log)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null)
            {
                log.LogWarning("Current identity has no user SID — skipping ACL hardening");
                return;
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            new DirectoryInfo(path).SetAccessControl(security);
            log.LogInformation("Directory ACLs restricted to current user: {Path}", path);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PrivilegeNotHeldException
                                      or IOException or PlatformNotSupportedException
                                      or DirectoryNotFoundException)
        {
            log.LogWarning(e, "Failed to restrict ACLs on {Path}", path);
        }
    }
}
