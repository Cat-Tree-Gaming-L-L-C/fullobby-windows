using Microsoft.Win32;

namespace Fullobby.Core.Activation;

/// <summary>
/// Housekeeping for the unpackaged Windows App SDK protocol registration.
/// <para><c>ActivationRegistrationManager.RegisterForProtocolActivation</c> mints a ProgId keyed to
/// the current exe path (<c>HKCU\Software\Classes\App.&lt;hash&gt;.Protocol</c>) and never removes the
/// one it made for a previous path. Running from a moved/updated install — or several dev-build
/// output paths — therefore leaves multiple live handlers, so Windows shows a "How do you want to
/// open this?" picker full of stale duplicate "Fullobby" entries on every <c>fullobby://</c>
/// OAuth callback. This prunes the ones that don't point at the running exe.</para>
/// </summary>
public static class ProtocolRegistration
{
    private const string ClassesKey = @"Software\Classes";

    /// <summary>
    /// Delete every <c>App.*.Protocol</c> ProgId under <c>HKCU\Software\Classes</c> whose open
    /// command launches <paramref name="exeFileName"/> from a path other than
    /// <paramref name="currentExePath"/>. Only handlers that reference our own exe are touched — a
    /// coincidental <c>App.&lt;hash&gt;.Protocol</c> from another app (e.g. ProtonVPN) is left alone.
    /// Best-effort: a key that can't be read or deleted is skipped. Returns the removed ProgId names.
    /// </summary>
    public static IReadOnlyList<string> PruneStaleHandlers(string exeFileName, string currentExePath)
    {
        var removed = new List<string>();
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesKey, writable: true);
            if (classes is null)
            {
                return removed;
            }

            foreach (var name in classes.GetSubKeyNames())
            {
                if (!name.StartsWith("App.", StringComparison.Ordinal)
                    || !name.EndsWith(".Protocol", StringComparison.Ordinal))
                {
                    continue;
                }

                var command = ReadOpenCommand(classes, name);
                if (command is null)
                {
                    continue;
                }

                // Only our exe (never another app's coincidental App.<hash>.Protocol) …
                if (command.IndexOf(exeFileName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                // … and keep the handler the running process just (re)registered.
                if (command.IndexOf(currentExePath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                try
                {
                    classes.DeleteSubKeyTree(name);
                    removed.Add(name);
                }
                catch
                {
                    // Best-effort: a locked/permission-denied key just stays. Not fatal.
                }
            }
        }
        catch
        {
            // Registry unavailable — housekeeping is non-essential, so swallow.
        }
        return removed;
    }

    private static string? ReadOpenCommand(RegistryKey classes, string progId)
    {
        try
        {
            using var cmd = classes.OpenSubKey($@"{progId}\shell\open\command", writable: false);
            return cmd?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
}
