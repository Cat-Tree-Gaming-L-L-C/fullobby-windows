use dioxus::prelude::*;

use crate::state::events::{
    cancel_autoseed, AUTOSEED_COUNTDOWN_ACTIVE, AUTOSEED_COUNTDOWN_REMAINING,
    AUTOSEED_COUNTDOWN_TYPE,
};
use crate::state::modal::show_confirm;
use crate::state::seeding::{
    SeedingRegion, SeedingStatus, IS_SEED_ALL, IS_SEEDING, SEEDING_ERROR_MESSAGE, SEEDING_INDEX,
    SEEDING_REGION, SEEDING_STATUS, SERVER_SWITCH_ACTIVE,
    SERVER_SWITCH_COUNTDOWN, SERVER_SWITCH_REASON, SERVER_SWITCH_SERVER_NAME,
    SERVER_SWITCH_SNOOZED, SERVER_SWITCH_SNOOZE_REMAINING,
};
use crate::state::servers::{EU_ENABLED, SERVERS_STATS, EU_SERVERS_STATS, SERVER_LOAD_ERROR};
use crate::state::session::EFFICIENCY_MODE;
use crate::state::toast::{add_toast, ToastType};

use crate::components::seed_banner::SeedBanner;

const SEED_ALL_COOLDOWN_SECS: u64 = 30;
const SEED_REGION_COOLDOWN_SECS: u64 = 5;

// ---------------------------------------------------------------------------
// Seeding action helpers
// ---------------------------------------------------------------------------

/// The core seeding logic: set state, call backend, and monitor.
/// Extracted so it can be called from both the normal path and the
/// "game was running, user confirmed kill" path.
async fn start_seeding_inner(server_number: usize, region: SeedingRegion) {
    *SEEDING_REGION.write() = region.clone();
    *SEEDING_INDEX.write() = Some(server_number);
    *IS_SEEDING.write() = true;
    *SEEDING_STATUS.write() = SeedingStatus::Initializing;

    // Set the current game for seeding
    let game = crate::backend::seeding::current_game();
    *crate::state::seeding::SEEDING_GAME.write() = Some(game.id.to_string());

    let result = match region {
        SeedingRegion::Eu => crate::backend::seeding::start_seeding_eu(server_number).await,
        SeedingRegion::Na => crate::backend::seeding::start_seeding(server_number).await,
    };

    match result {
        Ok(actual_index) => {
            *SEEDING_INDEX.write() = Some(actual_index);
            *SEEDING_STATUS.write() = SeedingStatus::Seeding;

            // Create session after successful launch (deferred from server selection)
            if crate::api::client::is_authenticated() {
                let region_str = match region {
                    SeedingRegion::Eu => "eu",
                    SeedingRegion::Na => "na",
                };
                let steam_id = crate::state::session::LINKED_STEAM_IDS
                    .read()
                    .first()
                    .cloned();

                let analytics = crate::api::client::gather_analytics(false);
                match crate::api::client::start_session(game.id, region_str, actual_index, steam_id.as_deref(), Some(analytics)).await {
                    Ok(resp) => {
                        *crate::state::session::ACTIVE_SESSION_ID.write() = Some(resp.session_id.clone());
                        crate::backend::heartbeat::start_heartbeat(resp.session_id).await;
                    }
                    Err(e) => {
                        tracing::warn!("Failed to create seeding session (non-fatal): {}", e);
                    }
                }
            }

            do_monitor_seed(actual_index, region).await;
        }
        Err(e) => {
            let err_str = format!("{}", e);
            if err_str.contains("could not open") {
                *SEEDING_STATUS.write() = SeedingStatus::WaitingForUpdate;
            } else if err_str.contains("Invalid") {
                tracing::error!("Critical error starting seed: {}", err_str);
                *SEEDING_ERROR_MESSAGE.write() = "Invalid configuration. Check settings and try again.".to_string();
                *SEEDING_STATUS.write() = SeedingStatus::Error;
                *SEEDING_INDEX.write() = None;
                *IS_SEEDING.write() = false;
            } else {
                tracing::error!("Error starting seed: {}", err_str);
                *SEEDING_ERROR_MESSAGE.write() = "Failed to launch game. Please try again.".to_string();
                *SEEDING_STATUS.write() = SeedingStatus::Error;
                // Still try to monitor in case game launched
                do_monitor_seed(server_number, region).await;
            }
        }
    }
}

/// Start seeding for a region. Fetches the best candidate from the status
/// endpoint, then launches. Spawns a background task.
fn do_start_seeding(_server_number: usize, region: SeedingRegion) {
    // Guard: don't start if already in progress
    let status = SEEDING_STATUS.read().clone();
    if matches!(
        status,
        SeedingStatus::Initializing
            | SeedingStatus::Seeding
            | SeedingStatus::Stopping
            | SeedingStatus::Switching
    ) {
        return;
    }

    spawn(async move {
        *SEEDING_STATUS.write() = SeedingStatus::Initializing;
        *IS_SEED_ALL.write() = false;

        let region_str = match region {
            SeedingRegion::Eu => "eu",
            SeedingRegion::Na => "na",
        };

        let status_result = crate::api::client::get_seeding_status().await;
        match status_result {
            Ok(status) => {
                let current_game_id = crate::backend::seeding::current_game().id;
                let game_status = match current_game_id {
                    "hll" => Some(&status.hll),
                    "hllv" => status.hllv.as_ref(),
                    _ => None,
                };

                let candidate = game_status.and_then(|gs| {
                    if region_str == "eu" { gs.eu.as_ref() } else { gs.na.as_ref() }
                });

                let Some(candidate) = candidate else {
                    add_toast("All servers in this region are full", ToastType::Info, None);
                    *SEEDING_STATUS.write() = SeedingStatus::Idle;
                    return;
                };

                let candidate_index = candidate.index;

                // If the game is already running, ask the user before killing it
                if crate::backend::process::is_game_running(crate::backend::seeding::current_game()) {
                    *SEEDING_STATUS.write() = SeedingStatus::Idle;

                    let game = crate::backend::seeding::current_game();
                    let confirmed = show_confirm(
                        &format!("{} is currently running. Close the game to start seeding?", game.display_name),
                        "Game Running",
                    )
                    .await;

                    if !confirmed {
                        return;
                    }

                    crate::backend::seeding::kill_game_process(game);

                    for _ in 0..40 {
                        if !crate::backend::process::is_game_running(game) {
                            break;
                        }
                        tokio::time::sleep(std::time::Duration::from_millis(500)).await;
                    }
                }

                start_seeding_inner(candidate_index, region).await;
            }
            Err(e) => {
                tracing::error!("Failed to fetch seeding status: {}", e);
                let msg = crate::api::client::friendly_error(e.as_ref());
                add_toast(
                    &format!("Failed to start seeding: {}", msg),
                    ToastType::Error,
                    None,
                );
                *SEEDING_STATUS.write() = SeedingStatus::Idle;
            }
        }
    });
}

/// Monitor seeding and handle completion.
/// If we have a session ID, stop heartbeat and check for next server.
/// Otherwise just reset to idle.
async fn do_monitor_seed(server_number: usize, region: SeedingRegion) {
    let idx = *SEEDING_INDEX.read();
    if idx.is_none() {
        return;
    }
    if idx != Some(server_number) {
        return;
    }

    let result = match region {
        SeedingRegion::Eu => crate::backend::seeding::monitor_seed_eu(server_number).await,
        SeedingRegion::Na => crate::backend::seeding::monitor_seed(server_number).await,
    };

    // Restore original settings after monitor completes — resets the
    // EFFICIENCY_MODE_APPLIED flag so efficiency can be re-applied on the
    // next seeding attempt (same session or after hide-to-tray).
    crate::backend::backup_restore_hll_config::restore_after_seeding();

    match result {
        Ok(()) => {}
        Err(e) => {
            let err_str = format!("{}", e);
            tracing::error!("Error monitoring seed: {}", err_str);
            if err_str.contains("HLL closed") {
                *SEEDING_STATUS.write() = SeedingStatus::Stopped;
                return;
            }
            *SEEDING_ERROR_MESSAGE.write() = "Seeding interrupted unexpectedly.".to_string();
            *SEEDING_STATUS.write() = SeedingStatus::Error;
        }
    }

    // Stop heartbeat and clear session (auth-gated)
    if crate::state::session::ACTIVE_SESSION_ID.read().is_some() {
        crate::backend::heartbeat::stop_heartbeat(Some("monitor_complete")).await;
        *crate::state::session::ACTIVE_SESSION_ID.write() = None;
    }

    // If this was a seed-all flow, try the next server
    let is_seed_all = *IS_SEED_ALL.read();
    if is_seed_all {
        let eu_enabled = *EU_ENABLED.read();
        let current_game_id = crate::backend::seeding::current_game().id.to_string();

        match crate::api::client::get_seeding_status().await {
            Ok(status) => {
                // Try current game first
                let is_na = matches!(region, SeedingRegion::Na);
                let game_status = match current_game_id.as_str() {
                    "hll" => Some(&status.hll),
                    "hllv" => status.hllv.as_ref(),
                    _ => None,
                };

                let candidate_pair = game_status.and_then(|gs| {
                    if is_na {
                        gs.na.as_ref()
                            .map(|c| (c, SeedingRegion::Na))
                            .or_else(|| {
                                if eu_enabled {
                                    gs.eu.as_ref().map(|c| (c, SeedingRegion::Eu))
                                } else {
                                    None
                                }
                            })
                    } else {
                        gs.eu.as_ref()
                            .map(|c| (c, SeedingRegion::Eu))
                            .or_else(|| {
                                if eu_enabled {
                                    gs.na.as_ref().map(|c| (c, SeedingRegion::Na))
                                } else {
                                    None
                                }
                            })
                    }
                });

                if let Some((c, next_region)) = candidate_pair {
                    Box::pin(start_seeding_inner(c.index, next_region)).await;
                } else {
                    // Current game exhausted — try next enabled game
                    let enabled_games = get_enabled_games();
                    let mut found_next = false;

                    for next_game_id in &enabled_games {
                        if *next_game_id == current_game_id {
                            continue;
                        }
                        let next_status = match next_game_id.as_str() {
                            "hll" => Some(&status.hll),
                            "hllv" => status.hllv.as_ref(),
                            _ => None,
                        };
                        if let Some(gs) = next_status {
                            let candidate = gs.na.as_ref()
                                .map(|c| (c, SeedingRegion::Na))
                                .or_else(|| {
                                    if eu_enabled {
                                        gs.eu.as_ref().map(|c| (c, SeedingRegion::Eu))
                                    } else {
                                        None
                                    }
                                });

                            if let Some((c, next_region)) = candidate {
                                // Switch to next game
                                if let Some(game_def) = crate::backend::game::by_id(next_game_id) {
                                    tracing::info!("Switching from {} to {} for cross-game seeding", current_game_id, next_game_id);

                                    // Kill current game
                                    let current_game = crate::backend::seeding::current_game();
                                    crate::backend::seeding::kill_game_process(current_game);
                                    tokio::time::sleep(std::time::Duration::from_secs(20)).await;

                                    // Set new game
                                    crate::backend::seeding::set_current_game(game_def);
                                    *crate::state::seeding::SEEDING_GAME.write() = Some(next_game_id.clone());

                                    Box::pin(start_seeding_inner(c.index, next_region)).await;
                                    found_next = true;
                                    break;
                                }
                            }
                        }
                    }

                    if !found_next {
                        tracing::info!("All games/servers exhausted, seeding complete");
                        *IS_SEED_ALL.write() = false;
                        *SEEDING_STATUS.write() = SeedingStatus::Idle;
                        *SEEDING_INDEX.write() = None;
                        *IS_SEEDING.write() = false;
                        *crate::state::seeding::SEEDING_GAME.write() = None;
                    }
                }
            }
            Err(e) => {
                tracing::error!("Failed to fetch seeding status: {}", e);
                *IS_SEED_ALL.write() = false;
                *SEEDING_STATUS.write() = SeedingStatus::Idle;
                *SEEDING_INDEX.write() = None;
                *IS_SEEDING.write() = false;
                *crate::state::seeding::SEEDING_GAME.write() = None;
            }
        }
    } else {
        // Manual single-server seed — just reset
        *SEEDING_STATUS.write() = SeedingStatus::Idle;
        *SEEDING_INDEX.write() = None;
        *IS_SEEDING.write() = false;
        *crate::state::seeding::SEEDING_GAME.write() = None;
    }
}

/// Stop seeding and kill game.
fn do_stop_seed() {
    *SEEDING_STATUS.write() = SeedingStatus::Stopping;
    *SEEDING_INDEX.write() = None;
    *IS_SEEDING.write() = false;
    *IS_SEED_ALL.write() = false;
    spawn(async move {
        // Stop heartbeat + notify API
        crate::backend::heartbeat::stop_heartbeat(Some("user_stopped")).await;
        *crate::state::session::ACTIVE_SESSION_ID.write() = None;
        *crate::state::seeding::SEEDING_GAME.write() = None;

        match crate::backend::seeding::stop_seeding().await {
            Ok(()) => {
                *SEEDING_STATUS.write() = SeedingStatus::Stopped;
            }
            Err(e) => {
                let err_str = format!("{}", e);
                tracing::error!("Error stopping seed: {}", err_str);
                if err_str.contains("HLL closed") {
                    *SEEDING_STATUS.write() = SeedingStatus::Stopped;
                } else {
                    *SEEDING_ERROR_MESSAGE.write() = "Failed to stop seeding cleanly.".to_string();
                    *SEEDING_STATUS.write() = SeedingStatus::Error;
                }
            }
        }
    });
}

/// Stop seeding but keep the game running.
fn do_stop_seed_only() {
    *SEEDING_STATUS.write() = SeedingStatus::Stopping;
    *SEEDING_INDEX.write() = None;
    *IS_SEEDING.write() = false;
    *IS_SEED_ALL.write() = false;
    crate::state::seeding::reset_server_switch();
    spawn(async move {
        // Stop heartbeat + notify API
        crate::backend::heartbeat::stop_heartbeat(Some("user_stopped_keep_game")).await;
        *crate::state::session::ACTIVE_SESSION_ID.write() = None;
        *crate::state::seeding::SEEDING_GAME.write() = None;

        match crate::backend::seeding::stop_seeding_only().await {
            Ok(()) => {
                *SEEDING_STATUS.write() = SeedingStatus::Stopped;
            }
            Err(e) => {
                tracing::error!("Error stopping seed only: {}", e);
                *SEEDING_ERROR_MESSAGE.write() = "Failed to stop seeding cleanly.".to_string();
                *SEEDING_STATUS.write() = SeedingStatus::Error;
            }
        }
    });
}

/// Snooze server switch by the given number of seconds.
fn do_snooze_server_switch(duration_secs: u64) {
    crate::backend::seeding::snooze_server_switch(duration_secs);
}

/// Confirm immediate server switch.
fn do_confirm_server_switch() {
    crate::backend::seeding::confirm_server_switch();
    crate::state::seeding::reset_server_switch();
}

/// Ask the API for the best server to seed and start seeding it.
fn do_seed_next_server() {
    // Guard: don't start if already in progress
    let status = SEEDING_STATUS.read().clone();
    if matches!(
        status,
        SeedingStatus::Initializing
            | SeedingStatus::Seeding
            | SeedingStatus::Stopping
            | SeedingStatus::Switching
    ) {
        return;
    }

    // Guard: cooldown active
    if crate::state::cooldown::is_on_cooldown("seed_all") {
        let remaining = crate::state::cooldown::remaining_secs("seed_all");
        add_toast(
            &format!("Please wait {}s before trying again", remaining + 1),
            ToastType::Info,
            None,
        );
        return;
    }

    spawn(async move {
        *SEEDING_STATUS.write() = SeedingStatus::Initializing;

        let eu_enabled = *EU_ENABLED.read();
        let enabled_games = get_enabled_games();

        let result = crate::api::client::get_seeding_status().await;

        match result {
            Ok(status) => {
                let mut candidate_pair: Option<(crate::api::types::RegionCandidate, SeedingRegion, String)> = None;

                // Iterate enabled games to find first available candidate
                for game_id in &enabled_games {
                    let game_status = match game_id.as_str() {
                        "hll" => Some(&status.hll),
                        "hllv" => status.hllv.as_ref(),
                        _ => None,
                    };

                    if let Some(gs) = game_status {
                        let found = gs.na.as_ref()
                            .map(|c| (c.clone(), SeedingRegion::Na, game_id.clone()))
                            .or_else(|| {
                                if eu_enabled {
                                    gs.eu.as_ref().map(|c| (c.clone(), SeedingRegion::Eu, game_id.clone()))
                                } else {
                                    None
                                }
                            });

                        if found.is_some() {
                            candidate_pair = found;
                            break;
                        }
                    }
                }

                let Some((candidate, region, game_id)) = candidate_pair else {
                    crate::state::cooldown::set_seed_all_cooldown(SEED_ALL_COOLDOWN_SECS);
                    add_toast(
                        "All servers are full or offline — no seeding needed",
                        ToastType::Info,
                        None,
                    );
                    *SEEDING_STATUS.write() = SeedingStatus::Idle;
                    return;
                };

                // Set current game
                if let Some(game_def) = crate::backend::game::by_id(&game_id) {
                    crate::backend::seeding::set_current_game(game_def);
                }
                *crate::state::seeding::SEEDING_GAME.write() = Some(game_id.clone());

                *IS_SEED_ALL.write() = true;

                // If game is already running, ask user before killing
                let current_game = crate::backend::seeding::current_game();
                if crate::backend::process::is_game_running(current_game) {
                    *SEEDING_STATUS.write() = SeedingStatus::Idle;

                    let confirmed = show_confirm(
                        &format!("{} is currently running. Close the game to start seeding?", current_game.display_name),
                        "Game Running",
                    )
                    .await;

                    if !confirmed {
                        *IS_SEED_ALL.write() = false;
                        return;
                    }

                    crate::backend::seeding::kill_game_process(current_game);

                    for _ in 0..40 {
                        if !crate::backend::process::is_game_running(current_game) {
                            break;
                        }
                        tokio::time::sleep(std::time::Duration::from_millis(500)).await;
                    }
                }

                start_seeding_inner(candidate.index, region).await;
            }
            Err(e) => {
                tracing::error!("Failed to fetch seeding status: {}", e);
                crate::state::cooldown::set_seed_all_cooldown(SEED_ALL_COOLDOWN_SECS);
                let msg = crate::api::client::friendly_error(e.as_ref());
                add_toast(
                    &format!("Failed to start seeding: {}", msg),
                    ToastType::Error,
                    None,
                );
                *SEEDING_STATUS.write() = SeedingStatus::Idle;
            }
        }
    });
}

/// Get the list of enabled game IDs from config, filtered to released games only.
fn get_enabled_games() -> Vec<String> {
    let released: Vec<&str> = crate::backend::game::RELEASED_GAMES.iter().map(|g| g.id).collect();
    crate::config::get("enabled_games")
        .and_then(|v| serde_json::from_value::<Vec<String>>(v).ok())
        .unwrap_or_else(|| vec!["hll".to_string()])
        .into_iter()
        .filter(|id| released.contains(&id.as_str()))
        .collect()
}

/// Retry loading servers from the API.
fn do_get_servers() {
    spawn(async move {
        crate::app::fetch_servers().await;
    });
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

#[component]
pub fn Seed() -> Element {
    // Seed All cooldown remaining (reactive — driven by countdown system, no polling)

    // Read core signals
    let status = SEEDING_STATUS.read().clone();
    let is_seeding = *IS_SEEDING.read();
    let efficiency_mode = *EFFICIENCY_MODE.read();
    let eu_enabled = *EU_ENABLED.read();
    let na_servers_count = SERVERS_STATS.read().len();
    let eu_servers_count = EU_SERVERS_STATS.read().len();
    let server_load_error = *SERVER_LOAD_ERROR.read();

    // Memoized autoseed state — only recomputes when autoseed signals change
    let autoseed = use_memo(move || {
        let active = *AUTOSEED_COUNTDOWN_ACTIVE.read();
        let remaining = *AUTOSEED_COUNTDOWN_REMAINING.read();
        let countdown_type = AUTOSEED_COUNTDOWN_TYPE.read().clone();
        (active, remaining, countdown_type)
    });
    let (autoseed_countdown_active, autoseed_countdown_remaining, autoseed_countdown_type) =
        autoseed.read().clone();

    // Memoized server-switch state — only recomputes when switch signals change
    let switch = use_memo(move || {
        let active = *SERVER_SWITCH_ACTIVE.read();
        let countdown = *SERVER_SWITCH_COUNTDOWN.read();
        let server_name = SERVER_SWITCH_SERVER_NAME.read().clone();
        let reason = SERVER_SWITCH_REASON.read().clone();
        let snoozed = *SERVER_SWITCH_SNOOZED.read();
        let snooze_remaining = *SERVER_SWITCH_SNOOZE_REMAINING.read();
        (active, countdown, server_name, reason, snoozed, snooze_remaining)
    });
    let (
        server_switch_active,
        server_switch_countdown,
        server_switch_server_name,
        server_switch_reason,
        server_switch_snoozed,
        server_switch_snooze_remaining,
    ) = switch.read().clone();

    // Determine button area top padding
    let compact_top = matches!(
        status,
        SeedingStatus::Initializing | SeedingStatus::Seeding
    );
    let padding_class = if compact_top {
        "flex flex-col w-full justify-center p-6 pt-2"
    } else {
        "flex flex-col w-full justify-center p-6"
    };

    // Whether we are in an idle-ish state showing the main seed buttons
    let show_seed_buttons = matches!(
        status,
        SeedingStatus::Idle
            | SeedingStatus::Stopped
            | SeedingStatus::Error
    ) || (matches!(status, SeedingStatus::Running) && !is_seeding);

    let show_waiting_for_update = matches!(status, SeedingStatus::WaitingForUpdate);

    let show_active_seeding = matches!(
        status,
        SeedingStatus::Initializing
            | SeedingStatus::Seeding
            | SeedingStatus::Stopping
            | SeedingStatus::Switching
    ) || (matches!(status, SeedingStatus::Running) && is_seeding);

    // Snooze minutes/seconds for display
    let snooze_minutes = server_switch_snooze_remaining / 60;
    let snooze_seconds = server_switch_snooze_remaining % 60;

    // Autoseed countdown type display
    let countdown_type_display = if autoseed_countdown_type == "eu" {
        "EU Servers"
    } else {
        "NA Servers"
    };

    // Server switch title
    let switch_title = if server_switch_reason == "candidate_changed" {
        "Server Switching"
    } else if server_switch_reason == "server_full" {
        "Server is Full"
    } else {
        "Time Limit Reached"
    };

    rsx! {
        // Global keydown handler: Escape cancels autoseed
        div {
            tabindex: "0",
            onkeydown: move |e: Event<KeyboardData>| {
                if e.key() == Key::Escape && *AUTOSEED_COUNTDOWN_ACTIVE.read() {
                    cancel_autoseed();
                }
            },

            SeedBanner {}

            // Main button area
            div { class: "{padding_class}",

                // ======================================================
                // Idle / Stopped / Running / Error: show seed buttons
                // ======================================================
                if show_seed_buttons {
                    // EU-enabled: show separate NA / EU buttons
                    if eu_enabled {
                        div { class: "w-full mb-2 flex flex-wrap",
                            // Seed NA button
                            button {
                                class: format!(
                                    "btn btn-ghost flex-col gap-1 w-1/2 h-24 text-lg{}",
                                    if na_servers_count == 0 { " opacity-40" } else { "" }
                                ),
                                onclick: move |_| {
                                    if server_load_error {
                                        do_get_servers();
                                    } else if !crate::state::cooldown::is_on_cooldown("seed_region") {
                                        crate::state::cooldown::set_cooldown("seed_region", SEED_REGION_COOLDOWN_SECS);
                                        do_start_seeding(0, SeedingRegion::Na);
                                    }
                                },
                                disabled: na_servers_count == 0 && !server_load_error,
                                title: if na_servers_count == 0 {
                                    if server_load_error { "Click to retry" } else { "Loading servers..." }
                                } else {
                                    "Seed the first available NA server"
                                },
                                if na_servers_count == 0 && server_load_error {
                                    span { class: "text-error text-xs", "Retry" }
                                } else if na_servers_count == 0 {
                                    span { class: "loading loading-spinner loading-sm" }
                                } else {
                                    img { src: asset!("/assets/globe_west.svg"), style: "width: 2rem; height: 2rem; object-fit: contain;", alt: "" }
                                }
                                span { "Seed NA" }
                            }
                            // Seed EU button
                            button {
                                class: format!(
                                    "btn btn-ghost flex-col gap-1 w-1/2 h-24 text-lg{}",
                                    if eu_servers_count == 0 { " opacity-40" } else { " opacity-80" }
                                ),
                                onclick: move |_| {
                                    if server_load_error {
                                        do_get_servers();
                                    } else if !crate::state::cooldown::is_on_cooldown("seed_region") {
                                        crate::state::cooldown::set_cooldown("seed_region", SEED_REGION_COOLDOWN_SECS);
                                        do_start_seeding(0, SeedingRegion::Eu);
                                    }
                                },
                                disabled: eu_servers_count == 0 && !server_load_error,
                                title: if eu_servers_count == 0 {
                                    if server_load_error { "Click to retry" } else { "Loading servers..." }
                                } else {
                                    "Seed the first available EU server"
                                },
                                if eu_servers_count == 0 && server_load_error {
                                    span { class: "text-error text-xs", "Retry" }
                                } else if eu_servers_count == 0 {
                                    span { class: "loading loading-spinner loading-sm" }
                                } else {
                                    img { src: asset!("/assets/globe_east.svg"), style: "width: 2rem; height: 2rem; object-fit: contain;", alt: "" }
                                }
                                span { "Seed EU" }
                            }
                        }
                    }

                    // Seed All button
                    {
                        let cooldown_secs = *crate::state::cooldown::SEED_ALL_COOLDOWN_REMAINING.read();
                        let on_cooldown = cooldown_secs > 0;
                        rsx! {
                            div { class: "flex",
                                button {
                                    class: format!(
                                        "btn btn-ghost flex-col gap-2 w-full h-24 text-xl{}",
                                        if na_servers_count == 0 || on_cooldown { " opacity-40" } else { "" }
                                    ),
                                    onclick: move |_| {
                                        if server_load_error {
                                            do_get_servers();
                                        } else {
                                            do_seed_next_server();
                                        }
                                    },
                                    disabled: (na_servers_count == 0 && !server_load_error) || on_cooldown,
                                    title: if on_cooldown {
                                        "Please wait before trying again"
                                    } else if na_servers_count == 0 {
                                        if server_load_error {
                                            "Click to retry loading servers"
                                        } else {
                                            "Loading servers..."
                                        }
                                    } else {
                                        "Seed all server groups in order"
                                    },

                                    if on_cooldown {
                                        span { class: "text-base-content/50 text-lg", "Retry in {cooldown_secs}s" }
                                    } else if na_servers_count == 0 && server_load_error {
                                        span { class: "text-error text-sm", "Failed to load servers" }
                                        span { class: "text-sm underline", "Tap to retry" }
                                    } else if na_servers_count == 0 {
                                        span { class: "loading loading-spinner loading-md" }
                                        span { "Loading servers..." }
                                    } else {
                                        img { src: asset!("/assets/logo_nbg.png"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "" }
                                        span { "Seed All" }
                                    }
                                }
                            }
                        }
                    }
                }

                // ======================================================
                // Waiting for update: show Cancel button
                // ======================================================
                if show_waiting_for_update {
                    button {
                        class: "btn btn-ghost flex-col gap-2 w-full h-24 text-xl",
                        onclick: move |_| do_stop_seed(),
                        img { src: asset!("/assets/stop_icon.svg"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "Cancel" }
                        span { "Cancel" }
                    }
                }

                // ======================================================
                // Active seeding: show Stop buttons
                // ======================================================
                if show_active_seeding {
                    div { class: "w-full flex",
                        div {
                            class: if efficiency_mode { "tooltip tooltip-bottom w-1/2" } else { "w-1/2" },
                            "data-tip": if efficiency_mode { "Game settings are modified for seeding" } else { "" },
                            button {
                                class: format!(
                                    "btn btn-ghost flex-col gap-2 w-full h-24 text-lg{}",
                                    if efficiency_mode { " btn-disabled opacity-30" } else { "" }
                                ),
                                onclick: move |_| {
                                    if !efficiency_mode {
                                        do_stop_seed_only();
                                    }
                                },
                                disabled: efficiency_mode,
                                title: "Stop seeding but keep the game running",
                                img { src: asset!("/assets/stop_icon.svg"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "Stop Seeding" }
                                span { "Stop Seeding" }
                            }
                        }
                        button {
                            class: "btn btn-outline btn-error flex-col gap-2 w-1/2 h-24 text-lg",
                            onclick: move |_| do_stop_seed(),
                            title: "Stop seeding and close the game",
                            img { src: asset!("/assets/stop_icon.svg"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "Stop Game" }
                            span { "Stop Game" }
                        }
                    }
                }
            }

            // ==============================================================
            // Auto-Seed Countdown Modal
            // ==============================================================
            dialog {
                class: format!(
                    "modal absolute{}",
                    if autoseed_countdown_active { " modal-open" } else { "" }
                ),
                style: "width: 100vw; height: 100vh;",
                div { class: "modal-box absolute inset-0 w-full h-full max-w-none flex flex-col items-center justify-center",
                    h3 { class: "font-bold text-lg", "Auto-Seed Starting" }
                    p { class: "py-4 text-center",
                        "{countdown_type_display} auto-seed starting in"
                    }
                    span {
                        class: "countdown font-mono text-4xl animate-pulse",
                        role: "timer",
                        aria_live: "assertive",
                        aria_label: "Auto-seed countdown: {autoseed_countdown_remaining} seconds remaining",
                        "{autoseed_countdown_remaining}s"
                    }
                    div { class: "modal-action",
                        button {
                            class: "btn btn-error btn-lg",
                            onclick: move |_| {
                                cancel_autoseed();
                            },
                            "Cancel"
                        }
                    }
                }
            }

            // ==============================================================
            // Server Switch Modal
            // ==============================================================
            dialog {
                class: format!(
                    "modal absolute{}",
                    if server_switch_active { " modal-open" } else { "" }
                ),
                style: "width: 100vw; height: 100vh;",
                div { class: "modal-box absolute inset-0 w-full h-full max-w-none flex flex-col items-center justify-center",
                    if server_switch_snoozed {
                        // Snoozed state
                        h3 { class: "font-bold text-lg", "Server Switch Snoozed" }
                        p { class: "py-4 text-center",
                            "Switching from "
                            span { class: "font-semibold", "{server_switch_server_name}" }
                            " in"
                        }
                        span {
                            class: "countdown font-mono text-4xl animate-pulse",
                            role: "timer",
                            aria_live: "assertive",
                            aria_label: "Server switch in {snooze_minutes} minutes {snooze_seconds} seconds",
                            "{snooze_minutes}m {snooze_seconds}s"
                        }
                        div { class: "modal-action flex-col items-center gap-2",
                            button {
                                class: "btn btn-warning btn-lg",
                                onclick: move |_| do_confirm_server_switch(),
                                "Switch Now"
                            }
                            if !efficiency_mode {
                                button {
                                    class: "btn btn-ghost",
                                    onclick: move |_| do_stop_seed_only(),
                                    "Stop Seeding"
                                }
                            }
                        }
                    } else {
                        // Active countdown (not snoozed)
                        h3 { class: "font-bold text-lg", "{switch_title}" }
                        p { class: "py-4 text-center",
                            "Switching from "
                            span { class: "font-semibold", "{server_switch_server_name}" }
                            " in"
                        }
                        span {
                            class: "countdown font-mono text-4xl animate-pulse",
                            role: "timer",
                            aria_live: "assertive",
                            aria_label: "Server switch in {server_switch_countdown} seconds",
                            "{server_switch_countdown}s"
                        }
                        div { class: "modal-action flex-col items-center gap-2",
                            div { class: "flex gap-2",
                                button {
                                    class: "btn btn-ghost",
                                    onclick: move |_| do_snooze_server_switch(300),
                                    title: "Snooze server switch for 5 minutes",
                                    "+5 min"
                                }
                                button {
                                    class: "btn btn-ghost",
                                    onclick: move |_| do_snooze_server_switch(900),
                                    title: "Snooze server switch for 15 minutes",
                                    "+15 min"
                                }
                                button {
                                    class: "btn btn-ghost",
                                    onclick: move |_| do_snooze_server_switch(1800),
                                    title: "Snooze server switch for 30 minutes",
                                    "+30 min"
                                }
                            }
                            button {
                                class: "btn btn-warning btn-lg",
                                onclick: move |_| do_confirm_server_switch(),
                                "Switch Now"
                            }
                            if !efficiency_mode {
                                button {
                                    class: "btn btn-ghost",
                                    onclick: move |_| do_stop_seed_only(),
                                    "Stop Seeding"
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
