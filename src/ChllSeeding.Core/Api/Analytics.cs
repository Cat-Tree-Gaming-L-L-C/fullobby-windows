using ChllSeeding.Core.Config;
using ChllSeeding.Core.Native;

namespace ChllSeeding.Core.Api;

/// <summary>
/// Gathers the non-PII analytics snapshot sent with a seeding session start.
/// Port of <c>gather_analytics</c> in <c>src-rust/src/api/client.rs</c> — reads the
/// efficiency-mode and EU-enabled toggles from config and the OS facts from
/// <see cref="OsInfo"/>. (Rust's <c>secondary_servers_enabled</c> key is the
/// clean-break-renamed <c>eu_enabled</c> here; the wire field stays <c>eu_enabled</c>.)
/// </summary>
public static class Analytics
{
    public static SessionStartAnalytics Gather(ConfigService config, bool autoSeed) => new(
        OsVersion: OsInfo.OsVersion,
        OsArch: OsInfo.OsArch,
        EfficiencyMode: config.GetBool("efficiency_mode"),
        EuEnabled: config.GetBool("eu_enabled"),
        AutoSeed: autoSeed);
}
