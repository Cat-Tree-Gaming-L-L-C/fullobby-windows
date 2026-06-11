using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ChllSeeder.Core.Tools;

/// <summary>
/// User-driven game-config backup/restore for the Tools tab — distinct from the automatic
/// efficiency-mode backup in <see cref="HllConfigBackupService"/>. Manual backups go to timestamped
/// folders under <c>%USERPROFILE%\chllseeder-backup\HLL\manual\</c> and are incremental (unchanged
/// files are hard-linked to the previous backup to save disk). Port of <c>backend/backup.rs</c>.
/// </summary>
public sealed class ManualBackupService
{
    /// <summary>Lightweight file metadata used to detect unchanged files between backups.</summary>
    public readonly record struct FileCompareInfo(long Size, DateTimeOffset? Modified);

    private readonly ILogger<ManualBackupService> _log;
    private readonly HllConfigBackupService _autoBackup;

    public ManualBackupService(ILogger<ManualBackupService> log, HllConfigBackupService autoBackup)
    {
        _log = log;
        _autoBackup = autoBackup;
    }

    private static string BackupBaseDir => Path.Combine(Branding.BackupRootDir, "HLL");
    private static string ManualDir => Path.Combine(BackupBaseDir, "manual");
    private static string AutoBackupFile => Path.Combine(BackupBaseDir, "auto", "GameUserSettings_backup.ini");

    /// <summary>The directory HLL stores its config in (the default the folder picker opens to).</summary>
    public static string DefaultConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HLL", "Saved", "Config", "WindowsNoEditor");

    /// <summary>Path to the manual-backup root (created lazily on first backup).</summary>
    public string GetManualBackupPath() => ManualDir;

    /// <summary>True when at least one timestamped manual backup exists.</summary>
    public bool HasBackup() => GetLatestManualBackup() is not null;

    /// <summary>True when an automatic (pre-seeding) backup exists.</summary>
    public bool HasAutoBackup() => File.Exists(AutoBackupFile);

    /// <summary>Restore the live config from the last automatic backup. Throws when none exists.</summary>
    public string RestoreFromAutoBackup()
    {
        if (!HasAutoBackup())
        {
            throw new InvalidOperationException("No automatic backup found");
        }
        _autoBackup.RestoreConfig();
        _autoBackup.InvalidateConfigCache();
        _log.LogInformation("User restored game settings from automatic backup");
        return "Settings restored from the last automatic backup.";
    }

    /// <summary>Open the application logs folder in Explorer. Port of <c>open_logs</c>.</summary>
    public void OpenLogs()
    {
        var dir = Branding.LogsDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Back up every file in <paramref name="sourceDir"/> to a fresh timestamped folder. Files
    /// unchanged since the previous backup are hard-linked; the rest are copied. Returns the count
    /// backed up. Port of <c>backup_user_settings</c>.
    /// </summary>
    public async Task<int> BackupUserSettingsAsync(string sourceDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException("Source path is not a directory");
        }

        var previousBackup = GetLatestManualBackup();
        var prevCache = previousBackup is null ? new Dictionary<string, FileCompareInfo>() : ReadDirMetadata(previousBackup);

        var sourceFiles = Directory.EnumerateFiles(sourceDir).ToList();
        if (sourceFiles.Count == 0)
        {
            throw new InvalidOperationException("No files found in folder.");
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var destDir = Path.Combine(ManualDir, timestamp);
        Directory.CreateDirectory(destDir);

        var copied = 0;
        var linked = 0;
        foreach (var filePath in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);

            // Only back up regular files (skip symlinks/reparse points).
            if (IsSymlink(filePath))
            {
                continue;
            }

            var destPath = Path.Combine(destDir, fileName);
            var sourceInfo = ReadFileInfo(filePath);
            var unchanged = IsFileUnchanged(sourceInfo, prevCache, fileName);

            if (unchanged && previousBackup is not null)
            {
                var prevFile = Path.Combine(previousBackup, fileName);
                if (File.Exists(prevFile) && !IsSymlink(prevFile) && TryHardLink(prevFile, destPath))
                {
                    linked++;
                    continue;
                }
            }

            try
            {
                await CopyFileAsync(filePath, destPath, ct).ConfigureAwait(false);
                copied++;
            }
            catch (IOException e)
            {
                _log.LogDebug(e, "Failed to back up {File}", fileName);
            }
        }

        var total = copied + linked;
        if (linked > 0)
        {
            _log.LogInformation("Incremental backup: {Copied} changed, {Linked} unchanged (linked), {Total} total",
                copied, linked, total);
        }
        else
        {
            _log.LogInformation("Backed up {Total} config files to {Dir}", total, destDir);
        }
        return total;
    }

    /// <summary>
    /// Restore every regular file from <paramref name="backupDir"/> into <paramref name="destDir"/>.
    /// Skips symlinks and anything resolving outside the two directories. Port of <c>restore_user_settings</c>.
    /// </summary>
    public async Task<int> RestoreUserSettingsAsync(string backupDir, string destDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(destDir))
        {
            throw new DirectoryNotFoundException("Destination path is not a directory");
        }
        if (!Directory.Exists(backupDir))
        {
            throw new DirectoryNotFoundException("Backup path is not a directory");
        }

        var backupFull = Path.GetFullPath(backupDir);
        var destFull = Path.GetFullPath(destDir);
        var restored = 0;

        foreach (var filePath in Directory.EnumerateFiles(backupDir))
        {
            ct.ThrowIfCancellationRequested();

            // Containment + symlink guards (TOCTOU mitigation, mirrors the Rust restore).
            if (!Path.GetFullPath(filePath).StartsWith(backupFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (IsSymlink(filePath))
            {
                continue;
            }

            var fileName = Path.GetFileName(filePath);
            var targetPath = Path.Combine(destDir, fileName);
            if (!Path.GetFullPath(targetPath).StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await CopyFileAsync(filePath, targetPath, ct).ConfigureAwait(false);
                restored++;
            }
            catch (IOException e)
            {
                _log.LogDebug(e, "Failed to restore {File}", fileName);
            }
        }

        _log.LogInformation("Restored {Count} config files from backup", restored);
        return restored;
    }

    // ── Pure helpers (public/static for unit coverage) ─────────────────────────

    /// <summary>
    /// Whether a folder name matches the backup timestamp format <c>YYYY-MM-DD_HH-MM-SS</c> (19 chars).
    /// Excludes user-renamed folders from incremental comparisons. Port of <c>is_timestamp_folder</c>.
    /// </summary>
    public static bool IsTimestampFolder(string name)
    {
        if (name.Length != 19)
        {
            return false;
        }
        if (name[4] != '-' || name[7] != '-' || name[10] != '_' || name[13] != '-' || name[16] != '-')
        {
            return false;
        }
        ReadOnlySpan<(int Start, int End)> digitRanges = [(0, 4), (5, 7), (8, 10), (11, 13), (14, 16), (17, 19)];
        foreach (var (start, end) in digitRanges)
        {
            for (var i = start; i < end; i++)
            {
                if (!char.IsAsciiDigit(name[i]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Whether a source file is unchanged vs the previous backup (same size, modified within 2s).
    /// Port of <c>is_file_unchanged_cached</c>.
    /// </summary>
    public static bool IsFileUnchanged(
        FileCompareInfo? source,
        IReadOnlyDictionary<string, FileCompareInfo> prevCache,
        string fileName)
    {
        if (source is not { } src)
        {
            return false;
        }
        if (!prevCache.TryGetValue(fileName, out var prev))
        {
            return false;
        }
        if (src.Size != prev.Size)
        {
            return false;
        }
        if (src.Modified is not { } s || prev.Modified is not { } p)
        {
            return false;
        }
        return Math.Abs((s - p).TotalSeconds) < 2;
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private static string? GetLatestManualBackup()
    {
        if (!Directory.Exists(ManualDir))
        {
            return null;
        }
        return Directory.EnumerateDirectories(ManualDir)
            .Where(d => IsTimestampFolder(Path.GetFileName(d)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static Dictionary<string, FileCompareInfo> ReadDirMetadata(string dir)
    {
        var cache = new Dictionary<string, FileCompareInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (ReadFileInfo(file) is { } info)
            {
                cache[Path.GetFileName(file)] = info;
            }
        }
        return cache;
    }

    private static FileCompareInfo? ReadFileInfo(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return new FileCompareInfo(fi.Length, fi.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private bool TryHardLink(string existing, string link)
    {
        try
        {
            if (CreateHardLinkW(link, existing, IntPtr.Zero))
            {
                return true;
            }
            _log.LogDebug("CreateHardLink failed ({Err}) — falling back to copy", Marshal.GetLastWin32Error());
            return false;
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Hard link failed — falling back to copy");
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
