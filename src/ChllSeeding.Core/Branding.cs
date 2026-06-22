namespace ChllSeeding.Core;

/// <summary>
/// CHLL Seeding brand identity — single source of truth for every identifier in
/// the rebrand checklist (docs/REWRITE_PLAN.md). Clean break from the old
/// Esprit-branded identifiers; no config migration.
/// </summary>
public static class Branding
{
    public const string ProductName = "CHLL Seeding";
    public const string Publisher = "Comp HLL";
    public const string ExecutableName = "CHLLSeeding.exe";

    /// <summary>Deep-link scheme used for OAuth callbacks (was espritseeder).</summary>
    public const string ProtocolScheme = "chllseeding";
    public const string ProtocolPrefix = "chllseeding://";
    public const string ProtocolDescription = "URL:CHLL Seeding Protocol";

    /// <summary>AppInstance single-instancing key (replaces the Global\EspritSeeder mutex).</summary>
    public const string SingleInstanceKey = "chll-seeding-main";

    /// <summary>HKCU Run value name for "Start with Windows".</summary>
    public const string StartupRunValueName = "CHLLSeeding";

    /// <summary>Config directory name under %APPDATA%.</summary>
    public const string ConfigDirName = "org.comphll.chllseeding";

    /// <summary>App data directory name under %LOCALAPPDATA% (logs live in a "logs" subdir).</summary>
    public const string LocalDataDirName = "CHLLSeeding";

    /// <summary>Backup root directory name under %USERPROFILE% (game config backups + efficiency-mode
    /// crash-recovery flag live here; was espritseeder-backup).</summary>
    public const string BackupDirName = "chllseeding-backup";

    /// <summary>Auto-seed scheduled task names.</summary>
    public const string ScheduledTaskNa = "CHLL-Seeding";
    public const string ScheduledTaskEu = "CHLL-Seeding-EU";

    /// <summary>Default API base URL (configurable via CHLL_SEEDING_API_URL).</summary>
    public const string DefaultApiBaseUrl = "https://seeding.comp-hll.org";

    /// <summary>%APPDATA%\org.comphll.chllseeding</summary>
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);

    /// <summary>%LOCALAPPDATA%\CHLLSeeding\logs</summary>
    public static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalDataDirName, "logs");

    /// <summary>%USERPROFILE%\chllseeding-backup</summary>
    public static string BackupRootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), BackupDirName);
}
