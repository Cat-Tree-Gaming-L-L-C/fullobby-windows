using Microsoft.Win32;

namespace ChllSeeding.Core.Native;

/// <summary>
/// Resolves and caches the Steam install location from the registry. Shared by
/// <see cref="SteamLauncher"/> and <see cref="ProcessMonitor"/> (port of the
/// <c>STEAM_PATH_CACHE</c> OnceCell + registry helpers in <c>steam.rs</c>), kept
/// standalone so those two don't depend on each other.
/// </summary>
public static class SteamPaths
{
    private const string RegistrySubKey = @"SOFTWARE\Wow6432Node\Valve\Steam";

    private static readonly object _gate = new();
    private static string? _cachedExePath;

    /// <summary>The cached full path to <c>steam.exe</c>, or null if not yet resolved.
    /// (Process-verification uses this to confirm a kill target lives under Steam.)</summary>
    public static string? CachedExePath
    {
        get { lock (_gate) { return _cachedExePath; } }
    }

    /// <summary>Full path to <c>steam.exe</c>, resolved from the registry and cached for the
    /// session. Throws <see cref="IOException"/>/<see cref="FileNotFoundException"/> if Steam
    /// isn't installed or the exe is missing.</summary>
    public static string GetExecutablePath()
    {
        lock (_gate)
        {
            if (_cachedExePath is not null)
            {
                return _cachedExePath;
            }
        }

        var installPath = ReadInstallPath();
        var exe = Path.Combine(installPath, "steam.exe");
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("Steam executable not found (check Steam installation)", exe);
        }

        lock (_gate)
        {
            _cachedExePath ??= exe;
            return _cachedExePath;
        }
    }

    /// <summary>Steam install directory (the folder containing <c>steam.exe</c>), from the registry.</summary>
    public static string GetInstallPath() => ReadInstallPath();

    private static string ReadInstallPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistrySubKey);
        if (key?.GetValue("InstallPath") is string path && path.Length > 0)
        {
            return path;
        }
        throw new IOException("Steam not found in the Windows Registry");
    }
}
