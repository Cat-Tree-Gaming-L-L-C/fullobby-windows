namespace Fullobby.Core.Games;

/// <summary>
/// Static definition of a supported game.
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

    /// <summary>Other names the main executable may run under. A process only counts as this game
    /// when its image lives in <see cref="InstallFolder"/>, so an alias shared with another game
    /// (both HLL titles may ship an <c>HLL-Win64-Shipping.exe</c>) can't be mistaken for it.</summary>
    public IReadOnlyList<string> AltExeNames { get; init; } = [];

    /// <summary><see cref="ExeName"/> followed by <see cref="AltExeNames"/>.</summary>
    public IReadOnlyList<string> ExeNames => [ExeName, .. AltExeNames];

    /// <summary>Text the game window's title contains, or null when unknown — the window is then
    /// found by its owning game process instead (see <c>WindowFocus</c>).</summary>
    public string? WindowTitle { get; init; }

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

/// <summary>The catalog of supported games.</summary>
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
        WindowTitle = "Hell Let Loose",
        ConfigRelativePath = @"HLL\Saved\Config\WindowsNoEditor\GameUserSettings.ini",
        SupportsEfficiencyMode = true,
    };

    /// <summary>Hell Let Loose: Vietnam. App id, install folder and launcher are Steam's published
    /// app config (3079210: installdir "Hell Let Loose - Vietnam", launch "Launch_HLL.exe"). The
    /// shipping exe is inferred from the Unreal project name its settings live under
    /// (<c>%LOCALAPPDATA%\HLLVietnam</c>), with the HLL name as a fallback alias; the install-folder
    /// check keeps either from matching the other game. The window title isn't published, so the
    /// window is found by process. Power savings stays off until its config format is known (its
    /// settings live under AppData, not the install folder).</summary>
    public static readonly GameDefinition Hllv = new()
    {
        Id = "hllv",
        DisplayName = "Hell Let Loose: Vietnam",
        SteamAppId = "3079210",
        ExeName = "HLLVietnam-Win64-Shipping.exe",
        AltExeNames = ["HLL-Win64-Shipping.exe"],
        LauncherExeName = "Launch_HLL.exe",
        InstallFolder = "Hell Let Loose - Vietnam",
        WindowTitle = null,
        ConfigRelativePath = null,
        SupportsEfficiencyMode = false,
    };

    /// <summary>All supported games, in priority order (HLL first).</summary>
    public static readonly IReadOnlyList<GameDefinition> All = [Hll, Hllv];

    /// <summary>Games the client will seed, in catalog order. Which of these a given machine can
    /// actually launch is <c>InstalledGames</c>' call.</summary>
    public static readonly IReadOnlyList<GameDefinition> Released = [Hll, Hllv];

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
