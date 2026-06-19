/// Game definitions for multi-game support.
///
/// Each game (HLL, HLLV) has its own Steam App ID, executable names,
/// install folder, and feature flags. All hardcoded HLL values are
/// centralized here so the rest of the codebase can be game-agnostic.

/// Static definition of a supported game.
pub struct GameDefinition {
    /// Short identifier used in API requests and config ("hll", "hllv").
    pub id: &'static str,
    /// Human-readable display name ("Hell Let Loose").
    pub display_name: &'static str,
    /// Steam App ID ("686810").
    pub steam_app_id: &'static str,
    /// Main game executable name ("HLL-Win64-Shipping.exe").
    pub exe_name: &'static str,
    /// EAC launcher executable name ("Launch_HLL.exe").
    pub launcher_exe_name: &'static str,
    /// Steam install folder name under steamapps/common/ ("Hell Let Loose").
    pub install_folder: &'static str,
    /// Relative path from install folder to GameUserSettings.ini, if any.
    /// None means config manipulation (efficiency mode) is not supported.
    pub config_relative_path: Option<&'static str>,
    /// Whether efficiency mode (low graphics) is supported for this game.
    pub supports_efficiency_mode: bool,
}

pub static HLL: GameDefinition = GameDefinition {
    id: "hll",
    display_name: "Hell Let Loose",
    steam_app_id: "686810",
    exe_name: "HLL-Win64-Shipping.exe",
    launcher_exe_name: "Launch_HLL.exe",
    install_folder: "Hell Let Loose",
    config_relative_path: Some("HLL\\Saved\\Config\\WindowsNoEditor\\GameUserSettings.ini"),
    supports_efficiency_mode: true,
};

pub static HLLV: GameDefinition = GameDefinition {
    id: "hllv",
    display_name: "HLLV",
    steam_app_id: "0",                     // TBD -- placeholder
    exe_name: "HLLV-Win64-Shipping.exe",   // TBD -- placeholder
    launcher_exe_name: "Launch_HLLV.exe",  // TBD -- placeholder
    install_folder: "HLLV",                // TBD -- placeholder
    config_relative_path: None,            // TBD -- no config manipulation until we know the paths
    supports_efficiency_mode: false,       // TBD -- disabled until we know HLLV's config format
};

/// All supported games, in priority order (HLL first).
pub static ALL_GAMES: &[&GameDefinition] = &[&HLL, &HLLV];

/// Games available for release (HLLV is scaffolded but not yet ready).
pub static RELEASED_GAMES: &[&GameDefinition] = &[&HLL];

/// Look up a game definition by its short ID.
pub fn by_id(id: &str) -> Option<&'static GameDefinition> {
    ALL_GAMES.iter().find(|g| g.id == id).copied()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_by_id_hll() {
        let game = by_id("hll").expect("should find HLL");
        assert_eq!(game.id, "hll");
        assert_eq!(game.display_name, "Hell Let Loose");
    }

    #[test]
    fn test_by_id_hllv() {
        let game = by_id("hllv").expect("should find HLLV");
        assert_eq!(game.id, "hllv");
        assert_eq!(game.display_name, "HLLV");
    }

    #[test]
    fn test_by_id_unknown_none_empty() {
        assert!(by_id("unknown").is_none());
        assert!(by_id("").is_none());
        assert!(by_id("HLL").is_none(), "lookup should be case-sensitive");
    }

    #[test]
    fn test_all_games_count_and_order() {
        assert_eq!(ALL_GAMES.len(), 2);
        assert_eq!(ALL_GAMES[0].id, "hll", "HLL should be first");
        assert_eq!(ALL_GAMES[1].id, "hllv");
    }

    #[test]
    fn test_hll_field_values() {
        assert_eq!(HLL.steam_app_id, "686810");
        assert_eq!(HLL.exe_name, "HLL-Win64-Shipping.exe");
        assert_eq!(HLL.launcher_exe_name, "Launch_HLL.exe");
        assert_eq!(HLL.install_folder, "Hell Let Loose");
        assert!(HLL.config_relative_path.is_some());
        assert!(HLL.supports_efficiency_mode);
    }

    #[test]
    fn test_hllv_field_values() {
        assert_eq!(HLLV.display_name, "HLLV");
        assert!(HLLV.config_relative_path.is_none());
        assert!(!HLLV.supports_efficiency_mode);
    }
}
