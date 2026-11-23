using Fullobby.Core.Config;

namespace Fullobby.Core.Games;

/// <summary>
/// Keeps <see cref="GameAvailability"/> across restarts, so a client that starts offline (or an
/// auto-seed wake that fires before the API answers) offers the games the registry last named and
/// honours the player's last-known "My games" rather than falling back to the defaults.
///
/// Stored as plain config strings: <c>games_registry</c> = <c>id:seeding:tested</c> items, and
/// <c>my_games</c> = <c>id:1</c>/<c>id:0</c> items in the player's order.
/// </summary>
public static class GameAvailabilityStore
{
    public const string RegistryKey = "games_registry";
    public const string ChoicesKey = "my_games";

    /// <summary>Apply whatever was stored last.</summary>
    public static void Load(ConfigService config)
    {
        if (ParseRegistry(config.GetString(RegistryKey)) is { Count: > 0 } registry)
        {
            GameAvailability.SetRegistry(registry);
        }
        if (ParseChoices(config.GetString(ChoicesKey)) is { } choices)
        {
            GameAvailability.SetChoices(choices);
        }
    }

    public static void SaveRegistry(ConfigService config, IEnumerable<(string Id, bool Seeding, bool Tested)> games) =>
        config.SetString(RegistryKey, string.Join(',',
            games.Select(g => $"{g.Id}:{(g.Seeding ? 1 : 0)}:{(g.Tested ? 1 : 0)}")));

    public static void SaveChoices(ConfigService config, IEnumerable<(string Id, bool OptedIn)> choices) =>
        config.SetString(ChoicesKey, string.Join(',', choices.Select(c => $"{c.Id}:{(c.OptedIn ? 1 : 0)}")));

    public static void ClearChoices(ConfigService config) => config.Remove(ChoicesKey);

    /// <summary>Parse a stored registry. Malformed items are skipped. Pure; public for tests.</summary>
    public static List<(string Id, bool Seeding, bool Tested)> ParseRegistry(string? raw)
    {
        var list = new List<(string, bool, bool)>();
        foreach (var item in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':');
            if (parts.Length == 3 && parts[0].Length > 0)
            {
                list.Add((parts[0], parts[1] == "1", parts[2] == "1"));
            }
        }
        return list;
    }

    /// <summary>Parse stored choices; null when there are none. Pure; public for tests.</summary>
    public static Dictionary<string, bool>? ParseChoices(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':');
            if (parts.Length == 2 && parts[0].Length > 0)
            {
                map[parts[0]] = parts[1] == "1";
            }
        }
        return map.Count > 0 ? map : null;
    }
}
