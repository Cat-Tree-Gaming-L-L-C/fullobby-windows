using Fullobby.Core.Config;
using Fullobby.Core.Games;
using Fullobby.Core.Native;

namespace Fullobby.Core.Api;

/// <summary>
/// Gathers the non-PII analytics snapshot sent with a seeding session start.
/// Reads the per-game efficiency-mode preference from config and the OS facts from <see cref="OsInfo"/>.
/// </summary>
public static class Analytics
{
    public static SessionStartAnalytics Gather(ConfigService config, GameDefinition game, bool autoSeed) => new(
        OsVersion: OsInfo.OsVersion,
        OsArch: OsInfo.OsArch,
        EfficiencyMode: EfficiencyPreference.IsEnabled(config, game),
        AutoSeed: autoSeed);
}
