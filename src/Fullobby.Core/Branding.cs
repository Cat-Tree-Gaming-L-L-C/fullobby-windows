namespace Fullobby.Core;

/// <summary>
/// Fullobby brand identity — single source of truth for every identifier in
/// the rebrand (docs/ARCHITECTURE.md). Fresh app identifiers; no config migration.
/// </summary>
public static class Branding
{
    public const string ProductName = "Fullobby";
    public const string Publisher = "Cat Tree Gaming LLC";
    public const string ExecutableName = "Fullobby.exe";

    /// <summary>Deep-link scheme used for OAuth callbacks.</summary>
    public const string ProtocolScheme = "fullobby";
    public const string ProtocolPrefix = "fullobby://";
    public const string ProtocolDescription = "URL:Fullobby Protocol";

    /// <summary>AppInstance single-instancing key.</summary>
    public const string SingleInstanceKey = "fullobby-main";

    /// <summary>HKCU Run value name for "Start with Windows".</summary>
    public const string StartupRunValueName = "Fullobby";

    /// <summary>Config directory name under %APPDATA%.</summary>
    public const string ConfigDirName = "com.fullobby.app";

    /// <summary>App data directory name under %LOCALAPPDATA% (logs live in a "logs" subdir).</summary>
    public const string LocalDataDirName = "Fullobby";

    /// <summary>Backup root directory name under %USERPROFILE% (game config backups + efficiency-mode
    /// crash-recovery flag live here).</summary>
    public const string BackupDirName = "fullobby-backup";

    /// <summary>Auto-seed scheduled task names.</summary>
    public const string ScheduledTaskNa = "Fullobby";
    public const string ScheduledTaskEu = "Fullobby-EU";

    /// <summary>Default (production) API base URL. Hardbaked into Release builds; the
    /// <c>FULLOBBY_API_URL</c> override only applies to Debug builds (see
    /// <see cref="Api.ApiConfig"/>).</summary>
    public const string DefaultApiBaseUrl = "https://api.fullobby.com";

    /// <summary>Default (production) self-update feed base URL — a static, GitHub Pages-hosted origin
    /// serving the signed <c>latest.json</c> / <c>latest-beta.json</c> manifests. Kept separate from the
    /// API so updates don't depend on the backend and the signed manifest is served verbatim (the API
    /// must never be in a position to strip the pinned-key signature). Hardbaked into Release builds; the
    /// <c>FULLOBBY_UPDATE_FEED_URL</c> override only applies to Debug builds (see
    /// <see cref="Update.UpdateConfig"/>).</summary>
    public const string DefaultUpdateFeedBaseUrl = "https://updates.fullobby.com";

    // ── Community / About links ──────────────────────────────────────────────
    // FAQ/Terms/Privacy are served by the seeding API itself, so they live under
    // DefaultApiBaseUrl.
    /// <summary>Community Discord invite (permanent redirect to the current invite).</summary>
    public const string DiscordUrl = "https://comp-hll.org/discord";
    /// <summary>Comp HLL community website.</summary>
    public const string WebsiteUrl = "https://comp-hll.org";
    /// <summary>Source / issue tracker (public repo under the Cat Tree Gaming LLC org).</summary>
    public const string GitHubUrl = "https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows";
    /// <summary>Seeding FAQ / "how it works" page (served by the API).</summary>
    public const string FaqUrl = DefaultApiBaseUrl + "/faq";
    /// <summary>Terms of service (served by the API).</summary>
    public const string TermsUrl = DefaultApiBaseUrl + "/terms";
    /// <summary>Privacy policy (served by the API).</summary>
    public const string PrivacyUrl = DefaultApiBaseUrl + "/privacy";

    /// <summary>%APPDATA%\com.fullobby.app</summary>
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);

    /// <summary>%LOCALAPPDATA%\Fullobby\logs</summary>
    public static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalDataDirName, "logs");

    /// <summary>%USERPROFILE%\fullobby-backup</summary>
    public static string BackupRootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), BackupDirName);
}
