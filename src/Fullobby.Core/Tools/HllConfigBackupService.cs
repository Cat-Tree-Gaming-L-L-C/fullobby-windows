using Fullobby.Core.Config;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Tools;

/// <summary>
/// Backs up and restores HLL's <c>GameUserSettings.ini</c> around "efficiency mode"
/// (low-graphics seeding), with a persistent crash-recovery flag so a crash mid-seed
/// is detected and the user's real settings restored on next startup. Thread-safe.
/// </summary>
public sealed class HllConfigBackupService
{
    private readonly ILogger<HllConfigBackupService> _log;
    private readonly string _configPath;   // HLL's GameUserSettings.ini
    private readonly string _backupDir;    // our backup folder
    private readonly string _flagPath;     // persistent efficiency-active flag file
    private readonly string _backupFilePath;

    private readonly object _cacheGate = new();
    private DateTime? _cachedMtime;
    private bool _cachedOverwritten;

    // 0 = not applied, 1 = applied this session (Interlocked-guarded).
    private int _efficiencyApplied;
    private int _startupRestoreOccurred;
    private int _startupRestoreFailed;

    /// <summary>Production constructor — uses HLL's real config path and the branded backup dir.</summary>
    public HllConfigBackupService(ILogger<HllConfigBackupService> log)
        : this(log, DefaultConfigPath(), Path.Combine(Branding.BackupRootDir, "HLL")) { }

    /// <summary>Testable constructor — points config + backup at arbitrary paths.
    /// <paramref name="backupBaseDir"/> is the "HLL" backup folder; the auto-backup lives in its
    /// "auto" subfolder and the flag file directly inside it.</summary>
    public HllConfigBackupService(ILogger<HllConfigBackupService> log, string configPath, string backupBaseDir)
    {
        _log = log;
        _configPath = configPath;
        _backupDir = Path.Combine(backupBaseDir, "auto");
        _flagPath = Path.Combine(backupBaseDir, ".efficiency_mode_active");
        _backupFilePath = Path.Combine(_backupDir, "GameUserSettings_backup.ini");
    }

    /// <summary>HLL stores user settings under %LOCALAPPDATA%\HLL\Saved\Config\WindowsNoEditor.</summary>
    private static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HLL", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");

    /// <summary>True if efficiency-mode settings have been applied this session.</summary>
    public bool IsEfficiencyModeApplied => Volatile.Read(ref _efficiencyApplied) == 1;

    // ── Startup crash recovery ────────────────────────────────────────────

    /// <summary>If efficiency mode was left active from a previous session (app crashed/closed
    /// during seeding), restore the backup and set a flag so the UI can show a toast later.</summary>
    public void CheckAndRestoreOnStartup()
    {
        if (!File.Exists(_flagPath))
        {
            return;
        }

        _log.LogInformation("Found leftover efficiency mode flag - app was closed during seeding");

        if (File.Exists(_backupFilePath))
        {
            _log.LogInformation("Restoring original settings from backup");
            RestoreConfig();
            ClearEfficiencyFlag();
            Volatile.Write(ref _efficiencyApplied, 0);
            Volatile.Write(ref _startupRestoreOccurred, 1);
        }
        else
        {
            _log.LogError("Efficiency mode flag found but no backup exists - cannot restore");
            ClearEfficiencyFlag();
            Volatile.Write(ref _efficiencyApplied, 0);
            Volatile.Write(ref _startupRestoreFailed, 1);
        }
    }

    /// <summary>Returns a message if a startup restore happened, clearing the flag. Call once after
    /// the UI is ready to show a toast.</summary>
    public string? TakeStartupRestoreNotice()
    {
        if (Interlocked.Exchange(ref _startupRestoreFailed, 0) == 1)
        {
            return "Efficiency mode was left on from a previous session, but no backup was found. "
                 + "Your game settings may need to be reconfigured manually.";
        }
        if (Interlocked.Exchange(ref _startupRestoreOccurred, 0) == 1)
        {
            return "Your game settings were restored automatically. "
                 + "The app was closed during seeding last session.";
        }
        return null;
    }

    // ── EULA overwrite detection ──────────────────────────────────────────

    /// <summary>Invalidate the config-overwritten cache (call after restore).</summary>
    public void InvalidateConfigCache()
    {
        lock (_cacheGate)
        {
            _cachedMtime = null;
        }
    }

    /// <summary>Whether HLL's config looks "overwritten" (EULA reset to 0 / missing file).
    /// Caches by file mtime to avoid re-reading on every call.</summary>
    public bool IsConfigOverwritten()
    {
        if (!File.Exists(_configPath))
        {
            return true;
        }

        DateTime currentMtime;
        try
        {
            currentMtime = File.GetLastWriteTimeUtc(_configPath);
        }
        catch
        {
            return ScanConfigFile();
        }

        lock (_cacheGate)
        {
            if (_cachedMtime == currentMtime)
            {
                return _cachedOverwritten;
            }
        }

        var result = ScanConfigFile();
        lock (_cacheGate)
        {
            _cachedMtime = currentMtime;
            _cachedOverwritten = result;
        }
        return result;
    }

    private bool ScanConfigFile()
    {
        try
        {
            foreach (var line in File.ReadLines(_configPath))
            {
                if (line.Contains("LastSeenEULAVersion=0", StringComparison.Ordinal))
                {
                    return true;
                }
                if (line.Contains("LastSeenEULAVersion=1", StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        catch
        {
            return true;
        }
        return true;
    }

    // ── Backup / restore ──────────────────────────────────────────────────

    /// <summary>Back up the current config to our auto-backup. Skipped if efficiency mode is active
    /// (the live config has degraded settings; the existing backup holds the real ones).</summary>
    public void BackupConfig()
    {
        if (IsEfficiencyModeApplied)
        {
            _log.LogInformation("Skipping backup — efficiency mode is active, existing backup has original settings");
            return;
        }
        if (File.Exists(_flagPath))
        {
            _log.LogInformation("Skipping backup — efficiency flag present, existing backup has original settings");
            return;
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(_configPath);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to read config file for backup");
            return;
        }

        try
        {
            Directory.CreateDirectory(_backupDir);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to create backup directory");
            return;
        }

        try
        {
            AtomicFile.WriteAllBytes(_backupFilePath, content);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to write backup file");
            return;
        }

        try
        {
            var written = new FileInfo(_backupFilePath).Length;
            if (written == content.Length)
            {
                _log.LogInformation("Config file successfully backed up ({Bytes} bytes)", content.Length);
            }
            else
            {
                _log.LogError("Backup size mismatch: expected {Expected} bytes, got {Actual}", content.Length, written);
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to verify backup file");
        }
    }

    /// <summary>Restore the config from our auto-backup. No-op if no backup exists.</summary>
    public void RestoreConfig()
    {
        _log.LogInformation("Restoring config file from backup");
        if (!File.Exists(_backupFilePath))
        {
            _log.LogInformation("No backup found, skipping restore");
            return;
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(_backupFilePath);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to read backup file");
            return;
        }

        try
        {
            AtomicFile.WriteAllBytes(_configPath, content);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to restore config");
            return;
        }

        _log.LogInformation("Config file restored from backup and synced to disk");
        InvalidateConfigCache();
    }

    // ── Efficiency mode apply / restore ───────────────────────────────────

    /// <summary>Apply efficiency-mode (low-graphics) settings to GameUserSettings.ini, after a
    /// write-ahead crash-recovery flag. Idempotent within a session.</summary>
    public void ApplyEfficiencySettings()
    {
        if (!File.Exists(_configPath))
        {
            _log.LogError("Config file does not exist, cannot apply efficiency settings");
            return;
        }

        // Atomically claim the flag — only one caller proceeds.
        if (Interlocked.CompareExchange(ref _efficiencyApplied, 1, 0) != 0)
        {
            _log.LogInformation("Efficiency settings already applied this session, skipping");
            return;
        }

        _log.LogInformation("Applying efficiency mode settings");

        string content;
        try
        {
            content = File.ReadAllText(_configPath);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to read config file");
            Volatile.Write(ref _efficiencyApplied, 0);
            return;
        }

        var modified = ApplyEfficiencyIniSettings(content, _log);

        // Write-ahead: set the persistent flag BEFORE writing settings. If we crash after the
        // flag but before the write, the next startup does a harmless no-op restore from the
        // still-original backup. The reverse order could strand the config in efficiency mode.
        SetEfficiencyFlag();

        try
        {
            AtomicFile.WriteAllBytes(_configPath, System.Text.Encoding.UTF8.GetBytes(modified));
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to write efficiency settings");
            ClearEfficiencyFlag(); // roll back the flag since the write failed
            Volatile.Write(ref _efficiencyApplied, 0);
            return;
        }

        _log.LogInformation("Efficiency mode settings applied and synced to disk");
        InvalidateConfigCache();
    }

    /// <summary>Restore the user's original settings after seeding (if efficiency mode was applied).</summary>
    public void RestoreAfterSeeding()
    {
        // Atomically claim the restore — only one caller proceeds.
        if (Interlocked.CompareExchange(ref _efficiencyApplied, 0, 1) != 1)
        {
            _log.LogInformation("Efficiency mode was not applied, skipping restore");
            return;
        }

        _log.LogInformation("Restoring original settings after seeding");
        RestoreConfig();
        ClearEfficiencyFlag();
    }

    // ── Persistent flag file ──────────────────────────────────────────────

    private void SetEfficiencyFlag()
    {
        try
        {
            var parent = Path.GetDirectoryName(_flagPath);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }
            File.WriteAllText(_flagPath, "1");
            _log.LogInformation("Efficiency mode flag set at {Path}", _flagPath);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to write efficiency mode flag");
        }
    }

    private void ClearEfficiencyFlag()
    {
        try
        {
            if (File.Exists(_flagPath))
            {
                File.Delete(_flagPath);
                _log.LogInformation("Efficiency mode flag cleared");
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to remove efficiency mode flag");
        }
    }

    // ── Pure INI transform (public + static for unit coverage) ────────────

    private static readonly (string Key, string Value)[] EfficiencySettings =
    [
        // Minimum supported resolution — 1024x768 windowed
        ("ResolutionSizeX", "1024"),
        ("ResolutionSizeY", "768"),
        ("LastUserConfirmedResolutionSizeX", "1024"),
        ("LastUserConfirmedResolutionSizeY", "768"),
        ("DesiredScreenWidth", "1024"),
        ("DesiredScreenHeight", "768"),
        ("LastUserConfirmedDesiredScreenWidth", "1024"),
        ("LastUserConfirmedDesiredScreenHeight", "768"),
        // Windowed mode (2 = windowed)
        ("FullscreenMode", "2"),
        ("LastConfirmedFullscreenMode", "2"),
        ("PreferredFullscreenMode", "2"),
        // Frame rate cap — 30 FPS (safe minimum)
        ("FrameRateLimit", "30.000000"),
        // All graphics to lowest
        ("sg.ResolutionQuality", "50.000000"),
        ("sg.ViewDistanceQuality", "0"),
        ("sg.AntiAliasingQuality", "0"),
        ("sg.ShadowQuality", "0"),
        ("sg.PostProcessQuality", "0"),
        ("sg.TextureQuality", "0"),
        ("sg.EffectsQuality", "0"),
        ("sg.FoliageQuality", "0"),
        ("sg.ShadingQuality", "0"),
        // Gameplay options to reduce clutter/rendering
        ("bGoreDisabled", "True"),
        ("bShowHints", "False"),
        ("bHideKickVoteRequests", "True"),
        ("bShowCommandMessages", "False"),
        ("bShowChatForNewMessages", "False"),
        ("DeadBodiesDespawnDelay", "30"),
        // Audio settings — low quality and muted
        ("AudioQualityLevel", "0"),
        ("MasterVolume", "0.000000"),
        ("MicrophoneVolume", "0.000000"),
    ];

    /// <summary>Rewrite the matching keys of an INI file's content to efficiency values,
    /// preserving every other line, normalizing to CRLF, and preserving the
    /// trailing-newline state of the input. Single pass, O(lines).</summary>
    public static string ApplyEfficiencyIniSettings(string content, ILogger? log = null)
    {
        var hasTrailingNewline = content.EndsWith('\n'); // covers "\n" and "\r\n"

        var settingsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in EfficiencySettings)
        {
            settingsMap[key] = value;
        }
        var foundKeys = new HashSet<string>(StringComparer.Ordinal);

        var result = new System.Text.StringBuilder(content.Length);
        var firstLine = true;

        foreach (var line in SplitLines(content))
        {
            if (!firstLine)
            {
                result.Append("\r\n");
            }
            firstLine = false;

            var eq = line.IndexOf('=');
            if (eq >= 0)
            {
                var key = line[..eq];
                if (settingsMap.TryGetValue(key, out var value))
                {
                    result.Append(key).Append('=').Append(value);
                    foundKeys.Add(key);
                    continue;
                }
            }
            result.Append(line);
        }

        if (log is not null)
        {
            foreach (var (key, _) in EfficiencySettings)
            {
                if (!foundKeys.Contains(key))
                {
                    log.LogInformation("Setting {Key}= not found in config, skipping", key);
                }
            }
        }

        if (hasTrailingNewline)
        {
            result.Append("\r\n");
        }
        return result.ToString();
    }

    /// <summary>Split text into lines: split on \n, strip a trailing \r,
    /// and drop a final empty segment from a trailing newline.</summary>
    private static IEnumerable<string> SplitLines(string content)
    {
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                var end = i;
                if (end > start && content[end - 1] == '\r')
                {
                    end--;
                }
                yield return content[start..end];
                start = i + 1;
            }
        }
        if (start < content.Length)
        {
            var end = content.Length;
            if (end > start && content[end - 1] == '\r')
            {
                end--;
            }
            yield return content[start..end];
        }
    }
}
