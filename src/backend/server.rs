use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex, RwLock};

use log::info;
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};

use crate::backend::game::GameDefinition;
use crate::error::AppError;

/// Server info from API (no server_public_url -- that stays server-side)
#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct ServerInfo {
    pub ip: String,
    pub bm_id: i64,
    pub short_name: String,
    pub name: String,
    pub seeding_threshold: i32,
    #[serde(default)]
    pub game: String,
}

// Store servers as Arc<ServerInfo> to avoid expensive deep clones of String fields
// RwLock allows concurrent reads without blocking
pub static SERVERS: Lazy<Arc<RwLock<Vec<Arc<ServerInfo>>>>> =
    Lazy::new(|| Arc::new(RwLock::new(Vec::new())));
pub static EU_SERVERS: Lazy<Arc<RwLock<Vec<Arc<ServerInfo>>>>> =
    Lazy::new(|| Arc::new(RwLock::new(Vec::new())));

/// Game-aware server storage: game -> region -> Vec<Arc<ServerInfo>>
pub static GAME_SERVERS: Lazy<Arc<RwLock<HashMap<String, HashMap<String, Vec<Arc<ServerInfo>>>>>>> =
    Lazy::new(|| Arc::new(RwLock::new(HashMap::new())));

// Track servers that are offline (set by API stats responses)
pub static OFFLINE_SERVERS: Lazy<Mutex<HashSet<String>>> =
    Lazy::new(|| Mutex::new(HashSet::new()));

// Current player counts: server_name -> (players, max_player_count)
// Updated from stats SSE/polling, readable from any context (no Dioxus runtime needed).
static SERVER_PLAYER_COUNTS: Lazy<Mutex<HashMap<String, (i32, i32)>>> =
    Lazy::new(|| Mutex::new(HashMap::new()));

// Server refresh intervals (used by background fetch loop)
pub const SERVER_REFRESH_INTERVAL_SECS: u64 = 300; // 5 minutes
pub const SERVER_REFRESH_MAX_BACKOFF_SECS: u64 = 3600; // 1 hour max backoff
pub const SERVER_REFRESH_MIN_INTERVAL_SECS: u64 = 60; // 1 minute on failure

fn get_server_list(region: &str) -> Vec<Arc<ServerInfo>> {
    let server_list = if region == "eu" { &*EU_SERVERS } else { &*SERVERS };
    read_lock!(server_list)
        .iter()
        .map(Arc::clone)
        .collect()
}

/// Safely get a server by index with bounds checking
pub fn get_server_by_region(region: &str, server_number: usize) -> Result<Arc<ServerInfo>, AppError> {
    let server_list = if region == "eu" { &*EU_SERVERS } else { &*SERVERS };
    let label = if region == "eu" { "EU server" } else { "server" };
    read_lock!(server_list)
        .get(server_number)
        .map(Arc::clone)
        .ok_or_else(|| AppError::new(format!("Invalid {} index: {}", label, server_number)))
}

/// Mark a server as offline
pub fn mark_server_offline(server_name: &str) {
    info!("Marking server as offline: {}", server_name);
    lock!(OFFLINE_SERVERS).insert(server_name.to_string());
}

/// Clear a server's offline status
pub fn clear_server_offline(server_name: &str) {
    lock!(OFFLINE_SERVERS).remove(server_name);
}

/// Clear all offline server statuses
#[allow(dead_code)]
pub fn clear_all_offline() {
    lock!(OFFLINE_SERVERS).clear();
}

/// Update cached player count for a server (called from stats updates).
pub fn update_player_count(server_name: &str, players: i32, max_players: i32) {
    lock!(SERVER_PLAYER_COUNTS).insert(server_name.to_string(), (players, max_players));
}

/// Get the last-known player count for a server: (players, max_player_count).
pub fn get_player_count(server_name: &str) -> Option<(i32, i32)> {
    lock!(SERVER_PLAYER_COUNTS).get(server_name).copied()
}

pub fn get_servers() -> Vec<Arc<ServerInfo>> {
    get_server_list("na")
}

pub fn get_eu_servers() -> Vec<Arc<ServerInfo>> {
    get_server_list("eu")
}

pub async fn is_hll_running() -> bool {
    crate::backend::process::is_process_running("HLL-Win64-Shipping.exe")
}

/// Check if a specific game is currently running.
pub async fn is_game_running_for(game: &GameDefinition) -> bool {
    crate::backend::process::is_game_running(game)
}

/// Get a server by game, region, and index.
pub fn get_game_server(game: &str, region: &str, index: usize) -> Result<Arc<ServerInfo>, AppError> {
    let store = read_lock!(GAME_SERVERS);
    store
        .get(game)
        .and_then(|regions| regions.get(region))
        .and_then(|servers| servers.get(index))
        .map(Arc::clone)
        .ok_or_else(|| AppError::new(format!("Invalid {} {} server index: {}", game, region, index)))
}

/// Get all servers for a game and region.
pub fn get_game_servers(game: &str, region: &str) -> Vec<Arc<ServerInfo>> {
    let store = read_lock!(GAME_SERVERS);
    store
        .get(game)
        .and_then(|regions| regions.get(region))
        .map(|servers| servers.iter().map(Arc::clone).collect())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_server(name: &str) -> Arc<ServerInfo> {
        Arc::new(ServerInfo {
            ip: "127.0.0.1".to_string(),
            bm_id: 1,
            short_name: name.to_string(),
            name: name.to_string(),
            seeding_threshold: 50,
            game: "hll".to_string(),
        })
    }

    // Helper: populate servers and run test, then clean up.
    // All server tests run in a single function to avoid parallel global-state conflicts.
    #[test]
    fn test_server_lookups_and_offline() {
        // ── Setup ──
        {
            let mut na = write_lock!(SERVERS);
            na.clear();
            na.push(make_server("NA-1"));
            na.push(make_server("NA-2"));
        }
        {
            let mut eu = write_lock!(EU_SERVERS);
            eu.clear();
            eu.push(make_server("EU-1"));
        }
        {
            let mut gs = write_lock!(GAME_SERVERS);
            gs.clear();
            let mut hll = HashMap::new();
            hll.insert("na".to_string(), vec![make_server("HLL-NA-1")]);
            hll.insert("eu".to_string(), vec![make_server("HLL-EU-1"), make_server("HLL-EU-2")]);
            gs.insert("hll".to_string(), hll);
        }

        // ── get_server_by_region: valid NA index ──
        let s = get_server_by_region("na", 0).unwrap();
        assert_eq!(s.short_name, "NA-1");

        let s = get_server_by_region("na", 1).unwrap();
        assert_eq!(s.short_name, "NA-2");

        // ── get_server_by_region: valid EU index ──
        let s = get_server_by_region("eu", 0).unwrap();
        assert_eq!(s.short_name, "EU-1");

        // ── get_server_by_region: out of bounds ──
        assert!(get_server_by_region("na", 99).is_err());
        assert!(get_server_by_region("eu", 1).is_err());

        // ── region routing: non-"eu" defaults to SERVERS ──
        let s = get_server_by_region("us", 0).unwrap();
        assert_eq!(s.short_name, "NA-1");

        // ── get_servers / get_eu_servers ──
        assert_eq!(get_servers().len(), 2);
        assert_eq!(get_eu_servers().len(), 1);

        // ── get_game_server: valid ──
        let s = get_game_server("hll", "na", 0).unwrap();
        assert_eq!(s.short_name, "HLL-NA-1");

        let s = get_game_server("hll", "eu", 1).unwrap();
        assert_eq!(s.short_name, "HLL-EU-2");

        // ── get_game_server: invalid game ──
        assert!(get_game_server("cs2", "na", 0).is_err());

        // ── get_game_server: invalid region ──
        assert!(get_game_server("hll", "oceania", 0).is_err());

        // ── get_game_server: invalid index ──
        assert!(get_game_server("hll", "na", 99).is_err());

        // ── get_game_servers: returns vec ──
        let v = get_game_servers("hll", "eu");
        assert_eq!(v.len(), 2);

        // ── get_game_servers: invalid game returns empty ──
        let v = get_game_servers("cs2", "na");
        assert!(v.is_empty());

        // ── get_game_servers: invalid region returns empty ──
        let v = get_game_servers("hll", "oceania");
        assert!(v.is_empty());

        // ── Offline tracking ──
        mark_server_offline("NA-1");
        assert!(lock!(OFFLINE_SERVERS).contains("NA-1"));

        clear_server_offline("NA-1");
        assert!(!lock!(OFFLINE_SERVERS).contains("NA-1"));

        mark_server_offline("NA-1");
        mark_server_offline("EU-1");
        assert_eq!(lock!(OFFLINE_SERVERS).len(), 2);

        clear_all_offline();
        assert!(lock!(OFFLINE_SERVERS).is_empty());

        // ── Player count tracking ──
        assert_eq!(get_player_count("NA-1"), None);

        update_player_count("NA-1", 42, 100);
        assert_eq!(get_player_count("NA-1"), Some((42, 100)));

        // Overwrite
        update_player_count("NA-1", 90, 100);
        assert_eq!(get_player_count("NA-1"), Some((90, 100)));

        // Different server
        update_player_count("EU-1", 10, 100);
        assert_eq!(get_player_count("EU-1"), Some((10, 100)));
        assert_eq!(get_player_count("NA-1"), Some((90, 100)));

        // Unknown server
        assert_eq!(get_player_count("NOPE"), None);

        // ── Cleanup ──
        write_lock!(SERVERS).clear();
        write_lock!(EU_SERVERS).clear();
        write_lock!(GAME_SERVERS).clear();
    }
}
