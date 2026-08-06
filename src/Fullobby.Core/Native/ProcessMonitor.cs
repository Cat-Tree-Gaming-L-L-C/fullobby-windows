using System.Runtime.InteropServices;
using Fullobby.Core.Games;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace Fullobby.Core.Native;

/// <summary>
/// Detects and terminates game/Steam processes via Toolhelp32 snapshots.
/// DI singleton, thread-safe.
/// </summary>
public sealed class ProcessMonitor
{
    private const uint AccessDenied = 5; // ERROR_ACCESS_DENIED
    private const long ProcessCacheTtlMs = 15_000;
    private const int MaxProcessCacheEntries = 10;

    private sealed class CachedCheck
    {
        public long Timestamp;
        public bool IsRunning;
        public uint? Pid;
    }

    private readonly ILogger<ProcessMonitor> _log;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedCheck> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProcessMonitor(ILogger<ProcessMonitor> log) => _log = log;

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Whether a process with the exact exe name is running (15s cache with a
    /// lightweight PID-validity fast path).</summary>
    public bool IsProcessRunning(string processName)
    {
        var now = Environment.TickCount64;

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(processName, out var cached)
                && now - cached.Timestamp < ProcessCacheTtlMs)
            {
                if (cached.Pid is { } pid)
                {
                    var stillRunning = IsPidValid(pid);
                    if (stillRunning == cached.IsRunning)
                    {
                        return cached.IsRunning;
                    }
                    _log.LogDebug("PID {Pid} state changed for '{Name}': cached={Cached}, actual={Actual}",
                        pid, processName, cached.IsRunning, stillRunning);
                }
                else if (!cached.IsRunning)
                {
                    return false;
                }
            }
        }

        var pids = SnapshotFindByName(processName);
        var found = pids.Count > 0;
        UpdateCache(processName, found, pids.Count > 0 ? pids[0] : null, evictIfFull: true);
        return found;
    }

    /// <summary>Fresh scan for <c>steam.exe</c>, bypassing the 15s cache.</summary>
    public bool IsSteamRunningFresh()
    {
        var pids = SnapshotFindByName("steam.exe");
        var found = pids.Count > 0;
        UpdateCache("steam.exe", found, pids.Count > 0 ? pids[0] : null, evictIfFull: false);
        return found;
    }

    /// <summary>Whether a game's main process is running.</summary>
    public bool IsGameRunning(GameDefinition game) => IsProcessRunning(game.ExeName);

    /// <summary>Whether a game's EAC launcher is running.</summary>
    public bool IsGameLoading(GameDefinition game) => IsProcessRunning(game.LauncherExeName);

    /// <summary>Check the EAC bootstrapper and the game process in a single fresh snapshot.
    /// Returns <c>(launcherRunning, exeRunning)</c>.</summary>
    public (bool LauncherRunning, bool ExeRunning) CheckGameLaunchProcesses(GameDefinition game)
    {
        var map = SnapshotFindByNames([game.LauncherExeName, game.ExeName]);

        map.TryGetValue(game.LauncherExeName, out var launcherPids);
        var launcherFound = launcherPids is { Count: > 0 };

        map.TryGetValue(game.ExeName, out var exePids);
        var exeFound = exePids is { Count: > 0 };

        UpdateCache(game.LauncherExeName, launcherFound, launcherFound ? launcherPids![0] : null, evictIfFull: false);
        UpdateCache(game.ExeName, exeFound, exeFound ? exePids![0] : null, evictIfFull: false);

        return (launcherFound, exeFound);
    }

    /// <summary>Kill all processes matching the game's exe name, but only after verifying each
    /// one actually lives under the Steam installation (never kills an unrelated process).</summary>
    public void KillGameProcesses(GameDefinition game)
    {
        var pids = SnapshotFindByName(game.ExeName);
        var folderLower = game.InstallFolder.ToLowerInvariant();

        foreach (var pid in pids)
        {
            var exePath = GetProcessImagePath(pid);
            if (exePath is null)
            {
                _log.LogDebug("Skipping PID {Pid} - path unavailable", pid);
                continue;
            }
            var pathLower = exePath.ToLowerInvariant();

            if (IsVerifiedGamePath(pathLower, folderLower))
            {
                _log.LogDebug("Terminating verified game process (PID {Pid})", pid);
                TerminatePid(pid);
            }
            else
            {
                _log.LogDebug("Skipping PID {Pid} - unverified path", pid);
            }
        }

        lock (_cacheGate)
        {
            _cache.Remove(game.ExeName);
        }
    }

    // ── Path verification (pure; public for unit coverage) ──

    /// <summary>Strict fallback check that a path is the HLL Steam install (used when the cached
    /// Steam dir is unavailable).</summary>
    public static bool IsHllSteamPath(string pathLower) =>
        pathLower.Contains(@"\steamapps\common\hell let loose\", StringComparison.Ordinal)
        || pathLower.Contains("/steamapps/common/hell let loose/", StringComparison.Ordinal);

    /// <summary>Generic fallback: a path under steamapps/common that contains the game's folder.</summary>
    public static bool IsGameSteamPath(string pathLower, string installFolderLower) =>
        (pathLower.Contains(@"\steamapps\common\", StringComparison.Ordinal)
            || pathLower.Contains("/steamapps/common/", StringComparison.Ordinal))
        && pathLower.Contains(installFolderLower, StringComparison.Ordinal);

    /// <summary>Decide whether a process path is a verified copy of the given game: prefer the
    /// cached Steam directory, fall back to the steamapps/common heuristic.</summary>
    private static bool IsVerifiedGamePath(string pathLower, string installFolderLower)
    {
        if (SteamPaths.CachedExePath is { } steamExe)
        {
            // Two levels up from steam.exe (…\Steam\steam.exe → the folder containing Steam).
            var steamDir = Path.GetDirectoryName(Path.GetDirectoryName(steamExe));
            if (steamDir is not null)
            {
                var steamDirLower = steamDir.ToLowerInvariant();
                return pathLower.StartsWith(steamDirLower, StringComparison.Ordinal)
                    && pathLower.Contains(installFolderLower, StringComparison.Ordinal);
            }
        }
        return IsGameSteamPath(pathLower, installFolderLower);
    }

    // ── Native helpers ──────────────────────────────────────────────────────

    private static bool IsPidValid(uint pid)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsNull)
        {
            return (uint)Marshal.GetLastWin32Error() == AccessDenied;
        }
        PInvoke.CloseHandle(handle);
        return true;
    }

    private static unsafe string? GetProcessImagePath(uint pid)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsNull)
        {
            return null;
        }
        try
        {
            Span<char> buffer = stackalloc char[1024];
            uint size = (uint)buffer.Length;
            fixed (char* p = buffer)
            {
                bool ok = PInvoke.QueryFullProcessImageName(
                    handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(p), &size);
                if (!ok)
                {
                    return null;
                }
                return new string(buffer[..(int)size]);
            }
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private void TerminatePid(uint pid)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, pid);
        if (handle.IsNull)
        {
            _log.LogDebug("Failed to open process {Pid} for termination", pid);
            return;
        }
        try
        {
            PInvoke.TerminateProcess(handle, 1);
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static List<uint> SnapshotFindByName(string name)
    {
        var map = SnapshotFindByNames([name]);
        return map.TryGetValue(name, out var pids) ? pids : [];
    }

    private static Dictionary<string, List<uint>> SnapshotFindByNames(IReadOnlyList<string> exeNames)
    {
        var result = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return result;
        }

        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (PInvoke.Process32FirstW(snapshot, ref entry))
        {
            do
            {
                var name = entry.szExeFile.ToString();
                foreach (var target in exeNames)
                {
                    if (string.Equals(target, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!result.TryGetValue(target, out var list))
                        {
                            list = [];
                            result[target] = list;
                        }
                        list.Add(entry.th32ProcessID);
                        break;
                    }
                }
            }
            while (PInvoke.Process32NextW(snapshot, ref entry));
        }

        return result;
    }

    private void UpdateCache(string name, bool isRunning, uint? pid, bool evictIfFull)
    {
        var now = Environment.TickCount64;
        lock (_cacheGate)
        {
            if (evictIfFull && !_cache.ContainsKey(name) && _cache.Count >= MaxProcessCacheEntries)
            {
                string? oldestKey = null;
                long oldest = long.MaxValue;
                foreach (var (k, v) in _cache)
                {
                    if (v.Timestamp < oldest)
                    {
                        oldest = v.Timestamp;
                        oldestKey = k;
                    }
                }
                if (oldestKey is not null)
                {
                    _cache.Remove(oldestKey);
                }
            }

            _cache[name] = new CachedCheck { Timestamp = now, IsRunning = isRunning, Pid = pid };
        }
    }
}
