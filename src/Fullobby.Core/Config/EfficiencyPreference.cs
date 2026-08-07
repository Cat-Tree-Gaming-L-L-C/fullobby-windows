using Fullobby.Core.Games;

namespace Fullobby.Core.Config;

/// <summary>
/// The per-game "Power Savings" (efficiency mode) preference. Chosen via the opt-in checkbox on
/// the seed surfaces (Seed button + auto-seed countdown) — not a global Settings toggle — and the
/// last choice is remembered per game across sessions. Games whose <see cref="GameDefinition"/>
/// doesn't support efficiency mode (e.g. Palworld) never surface the checkbox and always read off.
/// </summary>
public static class EfficiencyPreference
{
    /// <summary>The retired global Settings-page toggle key. Migrated once into the per-game
    /// keys by <see cref="MigrateLegacyGlobalToggle"/>, then dropped.</summary>
    private const string LegacyKey = "efficiency_mode";

    /// <summary>Config key remembering a game's checkbox state ("efficiency_mode.hll").</summary>
    public static string Key(GameDefinition game) => $"efficiency_mode.{game.Id}";

    /// <summary>Whether efficiency mode should be applied for <paramref name="game"/>: the game
    /// must support it AND the user's remembered checkbox must be on. Defaults to off.</summary>
    public static bool IsEnabled(ConfigService config, GameDefinition game) =>
        game.SupportsEfficiencyMode && config.GetBool(Key(game));

    /// <summary>Remember the user's checkbox choice for <paramref name="game"/>.</summary>
    public static void SetEnabled(ConfigService config, GameDefinition game, bool enabled) =>
        config.SetString(Key(game), enabled ? "true" : "false");

    /// <summary>One-time migration from the old global Settings toggle: if it was on, seed the
    /// per-game default for every game that supports efficiency mode (never clobbering a per-game
    /// choice that already exists), then drop the old key either way. No-op once the old key is gone.</summary>
    public static void MigrateLegacyGlobalToggle(ConfigService config)
    {
        var legacy = config.GetString(LegacyKey);
        if (legacy is null)
        {
            return; // already migrated (or never set)
        }

        if (legacy == "true")
        {
            foreach (var game in GameCatalog.All)
            {
                if (game.SupportsEfficiencyMode && config.GetString(Key(game)) is null)
                {
                    SetEnabled(config, game, true);
                }
            }
        }

        config.Remove(LegacyKey);
    }
}
