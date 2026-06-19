using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ChllSeeding.Core.Platform;

/// <summary>
/// "Start with Windows" via the HKCU Run key. Port of
/// <c>src-rust/src/platform/startup.rs</c> (winreg → <see cref="Microsoft.Win32.Registry"/>).
/// The value name is <see cref="Branding.StartupRunValueName"/> (was "EspritSeeder");
/// the data is the quoted current-exe path. Registered as a DI singleton.
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
        var value = QuotedExePath();
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
    /// If startup is enabled but points at a stale path (e.g. after the installer moved the exe),
    /// rewrite it to the current location. No-op when startup is disabled. Port of
    /// <c>update_startup_path_if_needed</c>; called once at launch.
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

            var expected = QuotedExePath();
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Updating startup path: {Old} -> {New}", current, expected);
                Enable();
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to update startup path");
        }
    }

    private static string QuotedExePath()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine current executable path");
        return $"\"{exe}\"";
    }
}
