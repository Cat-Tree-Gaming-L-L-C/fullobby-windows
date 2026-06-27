using ChllSeeding.Core.Config;
using ChllSeeding.Core.Native;

namespace ChllSeeding.Core.Api;

/// <summary>
/// Gathers the non-PII analytics snapshot sent with a seeding session start.
/// Reads the efficiency-mode and EU-enabled toggles from config and the OS facts from
/// <see cref="OsInfo"/>. (The EU-enabled toggle is sent as the <c>eu_enabled</c> wire field.)
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
