using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Fullobby.Core.Platform;

/// <summary>
/// "Start with Windows" via the HKCU Run key, using <see cref="Microsoft.Win32.Registry"/>.
/// The value name is <see cref="Branding.StartupRunValueName"/>; the data is the quoted current-exe
/// path followed by <see cref="Branding.MinimizedArg"/>, so a sign-in launch lands in the tray
/// rather than opening the window. Registered as a DI singleton.
/// <para>The installer's startup task writes the same value (<c>installer/Fullobby.iss</c>) — the
/// two must produce a byte-identical string, or <see cref="UpdatePathIfNeeded"/> rewrites setup's
/// entry on every launch.</para>
/// </summary>
public sealed class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ILogger<StartupRegistry> _log;

    public StartupRegistry(ILogger<StartupRegistry> log) => _log = log;

    /// <summary>True when a startup entry exists (regardless of its current target path).</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(Branding.StartupRunValueName) is not null;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to read startup registry value");
            return false;
        }
    }

    /// <summary>Add (or refresh) the startup entry pointing at the current executable.</summary>
    public void Enable()
    {
        var value = StartupCommand();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU Run key for writing");
        key.SetValue(Branding.StartupRunValueName, value, RegistryValueKind.String);
        _log.LogInformation("Startup enabled: {Value}", value);
    }

    /// <summary>Remove the startup entry. A missing value is treated as success.</summary>
    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Branding.StartupRunValueName) is not null)
            {
                key.DeleteValue(Branding.StartupRunValueName, throwOnMissingValue: false);
                _log.LogInformation("Startup disabled");
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to remove startup registry value");
            throw;
        }
    }

    /// <summary>
    /// If startup is enabled but the command is stale — a path from before an update moved the exe,
    /// or an entry written by a build that predates <see cref="Branding.MinimizedArg"/> — rewrite it.
    /// No-op when startup is disabled. Port of <c>update_startup_path_if_needed</c>; called once at
    /// launch.
    /// </summary>
    public void UpdatePathIfNeeded()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(Branding.StartupRunValueName) is not string current)
            {
                return; // not enabled — nothing to update
            }

            var expected = StartupCommand();
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Updating startup command: {Old} -> {New}", current, expected);
                Enable();
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to update startup path");
        }
    }

    /// <summary>The Run value data: the quoted exe path plus the tray flag.</summary>
    private static string StartupCommand()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine current executable path");
        return $"\"{exe}\" {Branding.MinimizedArg}";
    }
}
