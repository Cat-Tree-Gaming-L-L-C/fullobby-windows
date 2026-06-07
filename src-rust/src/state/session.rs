use dioxus::prelude::*;
use tracing::info;

use crate::backend::session as backend_session;

// Reactive state for session settings
pub static PLAYER_NAME: GlobalSignal<String> = Signal::global(String::new);
pub static EFFICIENCY_MODE: GlobalSignal<bool> = Signal::global(|| false);

// Session and steam linking state
pub static ACTIVE_SESSION_ID: GlobalSignal<Option<String>> = Signal::global(|| None);
pub static LINKED_STEAM_IDS: GlobalSignal<Vec<String>> = Signal::global(Vec::new);

// Linked providers state
pub static LINKED_PROVIDERS: GlobalSignal<Vec<crate::api::types::LinkedProvider>> = Signal::global(Vec::new);

/// Initialize all session state from persisted config
pub async fn initialize_session() {
    let name = backend_session::get_stored_session("player_name")
        .unwrap_or_default();
    let efficiency = backend_session::get_stored_session("efficiency_mode")
        .map(|v| v == "true")
        .unwrap_or(false);

    *PLAYER_NAME.write() = name;
    *EFFICIENCY_MODE.write() = efficiency;
}

/// Persist player name to session storage
pub fn save_player_name(name: &str) {
    *PLAYER_NAME.write() = name.to_string();
    if !name.is_empty() {
        if let Err(e) = backend_session::store_session("player_name", name.to_string()) {
            info!("Failed to persist player name: {}", e);
        }
    }
}

/// Persist efficiency mode setting
pub fn save_efficiency_mode(enabled: bool) {
    *EFFICIENCY_MODE.write() = enabled;
    let val = if enabled { "true" } else { "false" };
    if let Err(e) = backend_session::store_session("efficiency_mode", val.to_string()) {
        info!("Failed to persist efficiency_mode: {}", e);
    }
}

/// Refresh linked Steam IDs from the API and update state + config
pub async fn refresh_steam_ids_from_api() {
    match crate::api::client::get_steam_ids().await {
        Ok(entries) => {
            let ids: Vec<String> = entries.iter().map(|e| e.steam_id.clone()).collect();
            *LINKED_STEAM_IDS.write() = ids.clone();
            let ids_json = serde_json::to_string(&ids).unwrap_or_default();
            let _ = backend_session::store_session("linked_steam_ids", ids_json);
        }
        Err(e) => {
            info!("Failed to refresh steam IDs from API: {}", e);
        }
    }
}

/// Refresh linked providers from the API and update state
pub async fn refresh_linked_providers() {
    match crate::api::client::get_linked_providers().await {
        Ok(providers) => {
            *LINKED_PROVIDERS.write() = providers;
        }
        Err(e) => {
            info!("Failed to refresh linked providers: {}", e);
        }
    }
}
