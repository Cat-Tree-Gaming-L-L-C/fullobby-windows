use dioxus::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Server stats for display in the UI
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct ServerStats {
    pub name: String,
    pub map: String,
    pub players: String,
    pub max_player_count: Option<i32>,
    pub offline: bool,
}

/// Global state for NA servers
pub static SERVERS_STATS: GlobalSignal<Vec<ServerStats>> = Signal::global(Vec::new);
pub static EU_SERVERS_STATS: GlobalSignal<Vec<ServerStats>> = Signal::global(Vec::new);

/// Game-aware server stats: game -> region -> Vec<ServerStats>
pub static GAME_SERVER_STATS: GlobalSignal<HashMap<String, HashMap<String, Vec<ServerStats>>>> =
    Signal::global(HashMap::new);

pub static EU_ENABLED: GlobalSignal<bool> = Signal::global(|| false);
pub static LAST_STATS_UPDATE: GlobalSignal<u64> = Signal::global(|| 0);
pub static SERVER_LOAD_ERROR: GlobalSignal<bool> = Signal::global(|| false);
pub static SSE_CONNECTED: GlobalSignal<bool> = Signal::global(|| false);
pub static SSE_FAILURE_COUNT: GlobalSignal<u32> = Signal::global(|| 0);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_server_stats_default() {
        let stats = ServerStats::default();
        assert_eq!(stats.name, "");
        assert_eq!(stats.map, "");
        assert_eq!(stats.players, "");
        assert_eq!(stats.max_player_count, None);
        assert!(!stats.offline);
    }

    #[test]
    fn test_server_stats_serde_roundtrip() {
        let stats = ServerStats {
            name: "Esprit PF".to_string(),
            map: "Carentan".to_string(),
            players: "42/100".to_string(),
            max_player_count: Some(100),
            offline: false,
        };
        let json = serde_json::to_string(&stats).unwrap();
        let deserialized: ServerStats = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized, stats);
    }

    #[test]
    fn test_server_stats_deserialize_minimal() {
        let json = r#"{"name":"Test","map":"","players":"","offline":false}"#;
        let stats: ServerStats = serde_json::from_str(json).unwrap();
        assert_eq!(stats.name, "Test");
        assert_eq!(stats.max_player_count, None);
        assert!(!stats.offline);
    }

    #[test]
    fn test_server_stats_deserialize_offline() {
        let json = r#"{"name":"Down","map":"","players":"0/0","max_player_count":100,"offline":true}"#;
        let stats: ServerStats = serde_json::from_str(json).unwrap();
        assert!(stats.offline);
        assert_eq!(stats.max_player_count, Some(100));
    }

    #[test]
    fn test_server_stats_clone() {
        let stats = ServerStats {
            name: "Clone Test".to_string(),
            map: "Foy".to_string(),
            players: "50/100".to_string(),
            max_player_count: Some(100),
            offline: false,
        };
        let cloned = stats.clone();
        assert_eq!(stats, cloned);
    }

    #[test]
    fn test_server_stats_debug() {
        let stats = ServerStats::default();
        let debug = format!("{:?}", stats);
        assert!(debug.contains("ServerStats"));
    }
}
