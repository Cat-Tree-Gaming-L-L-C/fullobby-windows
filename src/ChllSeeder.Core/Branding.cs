namespace ChllSeeder.Core;

/// <summary>
/// CHLL Seeder brand identity — single source of truth for every identifier in
/// the rebrand checklist (docs/REWRITE_PLAN.md). Clean break from the old
/// Esprit-branded identifiers; no config migration.
/// </summary>
public static class Branding
{
    public const string ProductName = "CHLL Seeder";
    public const string Publisher = "Comp HLL";
    public const string ExecutableName = "CHLLSeeder.exe";

    /// <summary>Deep-link scheme used for OAuth callbacks (was espritseeder).</summary>
    public const string ProtocolScheme = "chllseeder";
    public const string ProtocolPrefix = "chllseeder://";
    public const string ProtocolDescription = "URL:CHLL Seeder Protocol";

    /// <summary>AppInstance single-instancing key (replaces the Global\EspritSeeder mutex).</summary>
    public const string SingleInstanceKey = "chll-seeder-main";

    /// <summary>HKCU Run value name for "Start with Windows".</summary>
    public const string StartupRunValueName = "CHLLSeeder";

    /// <summary>Config directory name under %APPDATA%.</summary>
    public const string ConfigDirName = "org.comphll.chllseeder";

    /// <summary>App data directory name under %LOCALAPPDATA% (logs live in a "logs" subdir).</summary>
    public const string LocalDataDirName = "CHLLSeeder";

    /// <summary>Auto-seed scheduled task names.</summary>
    public const string ScheduledTaskNa = "CHLL-Seeder";
    public const string ScheduledTaskEu = "CHLL-Seeder-EU";

    /// <summary>Default API base URL (configurable; exact host TBD).</summary>
    public const string DefaultApiBaseUrl = "https://seeding-api.comp-hll.org";

    /// <summary>%APPDATA%\org.comphll.chllseeder</summary>
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);

    /// <summary>%LOCALAPPDATA%\CHLLSeeder\logs</summary>
    public static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalDataDirName, "logs");
}
