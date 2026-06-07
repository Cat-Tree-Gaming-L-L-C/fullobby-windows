namespace ChllSeeder.Core.Games;

/// <summary>
/// Static definition of a supported game. Port of <c>src-rust/src/backend/game.rs</c>.
/// Each game (HLL, HLLV) has its own Steam App ID, executable names, install folder,
/// and feature flags so the rest of the codebase can be game-agnostic.
/// </summary>
public sealed record GameDefinition
{
    /// <summary>Short identifier used in API requests and config ("hll", "hllv").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name ("Hell Let Loose").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Steam App ID ("686810").</summary>
    public required string SteamAppId { get; init; }

    /// <summary>Main game executable name ("HLL-Win64-Shipping.exe").</summary>
    public required string ExeName { get; init; }

    /// <summary>EAC launcher executable name ("Launch_HLL.exe").</summary>
    public required string LauncherExeName { get; init; }

    /// <summary>Steam install folder name under steamapps/common/ ("Hell Let Loose").</summary>
    public required string InstallFolder { get; init; }

    /// <summary>Relative path from the install folder to GameUserSettings.ini, if any.
    /// <c>null</c> means config manipulation (efficiency mode) is not supported.</summary>
    public string? ConfigRelativePath { get; init; }

    /// <summary>Whether efficiency mode (low graphics) is supported for this game.</summary>
    public bool SupportsEfficiencyMode { get; init; }
}

/// <summary>The catalog of supported games. Port of the statics in <c>game.rs</c>.</summary>
public static class GameCatalog
{
    public static readonly GameDefinition Hll = new()
    {
        Id = "hll",
        DisplayName = "Hell Let Loose",
        SteamAppId = "686810",
        ExeName = "HLL-Win64-Shipping.exe",
        LauncherExeName = "Launch_HLL.exe",
        InstallFolder = "Hell Let Loose",
        ConfigRelativePath = @"HLL\Saved\Config\WindowsNoEditor\GameUserSettings.ini",
        SupportsEfficiencyMode = true,
    };

    public static readonly GameDefinition Hllv = new()
    {
        Id = "hllv",
        DisplayName = "HLLV",
        SteamAppId = "0",                      // TBD — placeholder
        ExeName = "HLLV-Win64-Shipping.exe",   // TBD — placeholder
        LauncherExeName = "Launch_HLLV.exe",   // TBD — placeholder
        InstallFolder = "HLLV",                // TBD — placeholder
        ConfigRelativePath = null,             // TBD — no config manipulation until paths known
        SupportsEfficiencyMode = false,        // TBD — disabled until HLLV's config format known
    };

    /// <summary>All supported games, in priority order (HLL first).</summary>
    public static readonly IReadOnlyList<GameDefinition> All = [Hll, Hllv];

    /// <summary>Games available for release (HLLV is scaffolded but not yet ready).</summary>
    public static readonly IReadOnlyList<GameDefinition> Released = [Hll];

    /// <summary>Look up a game definition by its short ID (case-sensitive). Null if unknown.</summary>
    public static GameDefinition? ById(string id)
    {
        foreach (var game in All)
        {
            if (game.Id == id)
            {
                return game;
            }
        }
        return null;
    }
}
