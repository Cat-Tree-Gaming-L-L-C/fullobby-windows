namespace ChllSeeding.Core;

/// <summary>
/// CHLL Seeding brand identity — single source of truth for every identifier in
/// the rebrand (docs/ARCHITECTURE.md). Fresh app identifiers; no config migration.
/// </summary>
public static class Branding
{
    public const string ProductName = "CHLL Seeding";
    public const string Publisher = "Comp HLL";
    public const string ExecutableName = "CHLLSeeding.exe";

    /// <summary>Deep-link scheme used for OAuth callbacks.</summary>
    public const string ProtocolScheme = "chllseeding";
    public const string ProtocolPrefix = "chllseeding://";
    public const string ProtocolDescription = "URL:CHLL Seeding Protocol";

    /// <summary>AppInstance single-instancing key.</summary>
    public const string SingleInstanceKey = "chll-seeding-main";

    /// <summary>HKCU Run value name for "Start with Windows".</summary>
    public const string StartupRunValueName = "CHLLSeeding";

    /// <summary>Config directory name under %APPDATA%.</summary>
    public const string ConfigDirName = "org.comphll.chllseeding";

    /// <summary>App data directory name under %LOCALAPPDATA% (logs live in a "logs" subdir).</summary>
    public const string LocalDataDirName = "CHLLSeeding";

    /// <summary>Backup root directory name under %USERPROFILE% (game config backups + efficiency-mode
    /// crash-recovery flag live here).</summary>
    public const string BackupDirName = "chllseeding-backup";

    /// <summary>Auto-seed scheduled task names.</summary>
    public const string ScheduledTaskNa = "CHLL-Seeding";
    public const string ScheduledTaskEu = "CHLL-Seeding-EU";

    /// <summary>Default (production) API base URL. Hardbaked into Release builds; the
    /// <c>CHLL_SEEDING_API_URL</c> override only applies to Debug builds (see
    /// <see cref="Api.ApiConfig"/>).</summary>
    public const string DefaultApiBaseUrl = "https://seeding.comp-hll.org";

    // ── Community / About links ──────────────────────────────────────────────
    // FAQ/Terms/Privacy are served by the seeding API itself, so they live under
    // DefaultApiBaseUrl.
    /// <summary>Community Discord invite (permanent redirect to the current invite).</summary>
    public const string DiscordUrl = "https://comp-hll.org/discord";
    /// <summary>Comp HLL community website.</summary>
    public const string WebsiteUrl = "https://comp-hll.org";
    /// <summary>Source / issue tracker (public repo under the Cat Tree Gaming LLC org).</summary>
    public const string GitHubUrl = "https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows";
    /// <summary>Seeding FAQ / "how it works" page (served by the API).</summary>
    public const string FaqUrl = DefaultApiBaseUrl + "/faq";
    /// <summary>Terms of service (served by the API).</summary>
    public const string TermsUrl = DefaultApiBaseUrl + "/terms";
    /// <summary>Privacy policy (served by the API).</summary>
    public const string PrivacyUrl = DefaultApiBaseUrl + "/privacy";

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
