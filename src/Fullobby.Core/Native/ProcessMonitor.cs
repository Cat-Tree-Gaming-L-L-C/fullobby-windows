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

    /// <summary>Whether a game's main process is running — any of its <see cref="GameDefinition.ExeNames"/>
    /// whose image lives in its install folder (15s cache, like <see cref="IsProcessRunning"/>). The
    /// folder check is what tells the two HLL titles apart: they share a launcher name and may share
    /// the shipping exe name.</summary>
    public bool IsGameRunning(GameDefinition game) =>
        IsRunningCached(GameKey(game), () => FindGamePids(game, game.ExeNames));

    /// <summary>Whether any released game's main process is running.</summary>
    public bool IsAnyGameRunning() => GameCatalog.Released.Any(IsGameRunning);

    /// <summary>Whether a game's EAC launcher is running (folder-verified, see <see cref="IsGameRunning"/>).</summary>
    public bool IsGameLoading(GameDefinition game) =>
        IsRunningCached(LauncherKey(game), () => FindGamePids(game, [game.LauncherExeName]));

    /// <summary>Pids of the game's main process (folder-verified). Used to find its window when the
    /// title isn't known.</summary>
    public IReadOnlyList<uint> GetGamePids(GameDefinition game) => FindGamePids(game, game.ExeNames);

    /// <summary>Check the EAC bootstrapper and the game process in a single fresh snapshot.
    /// Returns <c>(launcherRunning, exeRunning)</c>.</summary>
    public (bool LauncherRunning, bool ExeRunning) CheckGameLaunchProcesses(GameDefinition game)
    {
        var map = SnapshotFindByNames([game.LauncherExeName, .. game.ExeNames]);

        var launcherPids = VerifiedPids(game, map, [game.LauncherExeName]);
        var exePids = VerifiedPids(game, map, game.ExeNames);

        UpdateCache(LauncherKey(game), launcherPids.Count > 0, launcherPids.Count > 0 ? launcherPids[0] : null, evictIfFull: false);
        UpdateCache(GameKey(game), exePids.Count > 0, exePids.Count > 0 ? exePids[0] : null, evictIfFull: false);

        return (launcherPids.Count > 0, exePids.Count > 0);
    }

    /// <summary>Kill all of the game's main processes, but only those verified to live in the game's
    /// Steam install folder (never kills an unrelated process — or the other HLL title).</summary>
    public void KillGameProcesses(GameDefinition game)
    {
        foreach (var pid in FindGamePids(game, game.ExeNames))
        {
            _log.LogDebug("Terminating verified game process (PID {Pid})", pid);
            TerminatePid(pid);
        }

        lock (_cacheGate)
        {
            _cache.Remove(GameKey(game));
        }
    }

    // ── Path verification (pure; public for unit coverage) ──

    /// <summary>Strict fallback check that a path is the HLL Steam install.</summary>
    public static bool IsHllSteamPath(string pathLower) =>
        IsGameSteamPath(pathLower, GameCatalog.Hll.InstallFolder.ToLowerInvariant());

    /// <summary>A path inside <c>steamapps/common/&lt;installFolder&gt;/</c> of any Steam library. The
    /// folder must be a whole path segment: "hell let loose" is a prefix of "hell let loose - vietnam",
    /// so a plain substring test would count one game's processes as the other's.</summary>
    public static bool IsGameSteamPath(string pathLower, string installFolderLower) =>
        pathLower.Contains($@"\steamapps\common\{installFolderLower}\", StringComparison.Ordinal)
        || pathLower.Contains($"/steamapps/common/{installFolderLower}/", StringComparison.Ordinal);

    private static string GameKey(GameDefinition game) => $"game:{game.Id}";

    private static string LauncherKey(GameDefinition game) => $"launcher:{game.Id}";

    private List<uint> FindGamePids(GameDefinition game, IReadOnlyList<string> names) =>
        VerifiedPids(game, SnapshotFindByNames(names), names);

    /// <summary>The pids in <paramref name="map"/> under any of <paramref name="names"/> whose image is in
    /// the game's install folder. A process whose path can't be read is only trusted when no other
    /// game could own that name.</summary>
    private List<uint> VerifiedPids(
        GameDefinition game, Dictionary<string, List<uint>> map, IReadOnlyList<string> names)
    {
        var folderLower = game.InstallFolder.ToLowerInvariant();
        var result = new List<uint>();
        foreach (var name in names)
        {
            if (!map.TryGetValue(name, out var pids))
            {
                continue;
            }
            foreach (var pid in pids)
            {
                var exePath = GetProcessImagePath(pid);
                if (exePath is null)
                {
                    if (!IsNameShared(game, name))
                    {
                        result.Add(pid);
                    }
                    else
                    {
                        _log.LogDebug("Skipping PID {Pid} ({Name}) - path unavailable and name is shared", pid, name);
                    }
                    continue;
                }
                if (IsGameSteamPath(exePath.ToLowerInvariant(), folderLower))
                {
                    result.Add(pid);
                }
            }
        }
        return result;
    }

    private static bool IsNameShared(GameDefinition game, string name) =>
        GameCatalog.All.Any(g => g.Id != game.Id
            && (g.LauncherExeName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || g.ExeNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))));

    /// <summary>Cached running check keyed by <paramref name="key"/> (same TTL + PID fast path as
    /// <see cref="IsProcessRunning"/>).</summary>
    private bool IsRunningCached(string key, Func<List<uint>> find)
    {
        var now = Environment.TickCount64;
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var cached) && now - cached.Timestamp < ProcessCacheTtlMs)
            {
                if (cached.Pid is { } pid)
                {
                    if (IsPidValid(pid) == cached.IsRunning)
                    {
                        return cached.IsRunning;
                    }
                }
                else if (!cached.IsRunning)
                {
                    return false;
                }
            }
        }

        var pids = find();
        UpdateCache(key, pids.Count > 0, pids.Count > 0 ? pids[0] : null, evictIfFull: true);
        return pids.Count > 0;
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
                // Compare against the fixed-size buffer directly and only materialize a string on a
                // match. Calling ToString() per entry allocated once for every process on the
                // machine — and CheckGameLaunchProcesses deliberately bypasses the cache and runs
                // every 1–3s for up to 3 minutes during a launch, so this was tens of thousands of
                // throwaway strings at exactly the moment the machine is busy starting a game.
                var nameSpan = entry.szExeFile.AsReadOnlySpan();
                var end = nameSpan.IndexOf('\0');
                if (end >= 0)
                {
                    nameSpan = nameSpan[..end];
                }

                foreach (var target in exeNames)
                {
                    if (nameSpan.Equals(target, StringComparison.OrdinalIgnoreCase))
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
