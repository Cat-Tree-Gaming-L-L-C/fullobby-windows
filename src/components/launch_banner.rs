use dioxus::prelude::*;

use crate::components::server_stats_list::ServerStatsList;
use crate::state::seeding::{
    SeedingRegion, SeedingStatus, SEEDING_ERROR_MESSAGE, SEEDING_INDEX, SEEDING_REGION,
    SEEDING_STATUS,
};
use crate::state::servers::{EU_ENABLED, EU_SERVERS_STATS, SSE_CONNECTED, SERVERS_STATS};
use crate::state::session::EFFICIENCY_MODE;
use crate::state::stale_timer::STALE_INFO;

/// Banner shown above the launch buttons, displaying server stats for NA (and EU
/// if enabled) along with a stale-data warning when stats haven't refreshed recently.
#[component]
pub fn LaunchBanner() -> Element {
    let status = SEEDING_STATUS.read().clone();
    let seeding_index = *SEEDING_INDEX.read();
    let seeding_region = SEEDING_REGION.read().clone();
    let efficiency_mode = *EFFICIENCY_MODE.read();
    let eu_enabled = *EU_ENABLED.read();
    let servers_stats = SERVERS_STATS.read();
    let eu_servers_stats = EU_SERVERS_STATS.read();

    let is_active = matches!(
        status,
        SeedingStatus::Initializing
            | SeedingStatus::Seeding
            | SeedingStatus::Running
            | SeedingStatus::Switching
            | SeedingStatus::WaitingForUpdate
    );

    // Read backend server lists for counts and capacity
    let na_servers = crate::backend::server::get_servers();
    let eu_servers = crate::backend::server::get_eu_servers();

    let na_seeding_threshold: Vec<i32> = na_servers.iter().map(|s| s.seeding_threshold).collect();
    let eu_seeding_threshold: Vec<i32> = eu_servers.iter().map(|s| s.seeding_threshold).collect();

    // Derive status banner info
    let status_class = match &status {
        SeedingStatus::Running | SeedingStatus::Seeding => "alert alert-success",
        SeedingStatus::Error => "alert alert-error",
        SeedingStatus::Idle => "",
        _ => "alert",
    };
    let server_name = match seeding_index {
        Some(idx) => {
            let stats = match seeding_region {
                SeedingRegion::Eu => &*eu_servers_stats,
                SeedingRegion::Na => &*servers_stats,
            };
            stats.get(idx).map(|s| s.name.clone()).unwrap_or_default()
        }
        None => String::new(),
    };
    let phase_suffix = if seeding_region == SeedingRegion::Eu { " (EU)" } else { "" };
    let phase_suffix_short = if seeding_region == SeedingRegion::Eu { " EU" } else { "" };

    rsx! {
        // Status banner (game running / seeding / initializing etc.)
        match (&status, seeding_index) {
            (SeedingStatus::Idle, _) => rsx! {},
            (SeedingStatus::Seeding, Some(_)) => rsx! {
                div { class: "shadow-sm p-0 mx-5 w-auto justify-center {status_class}", role: "status",
                    span { "Seeding {server_name}{phase_suffix}" }
                }
            },
            (SeedingStatus::Initializing, Some(_)) => rsx! {
                div { class: "shadow-sm p-0 mx-5 w-auto justify-center {status_class}", role: "status",
                    span { "Initializing {server_name}{phase_suffix}" }
                }
            },
            (SeedingStatus::WaitingForUpdate, Some(_)) => rsx! {
                div { class: "shadow-sm p-0 mx-5 w-auto justify-center {status_class}", role: "status",
                    span { "Waiting for game update ({server_name}{phase_suffix_short})" }
                }
            },
            (SeedingStatus::Error, _) => {
                let err_msg = SEEDING_ERROR_MESSAGE.read();
                let display_msg = if err_msg.is_empty() {
                    "An error occurred. Please try again.".to_string()
                } else {
                    err_msg.clone()
                };
                rsx! {
                    div { class: "shadow-sm p-0 mx-5 w-auto justify-center {status_class}", role: "status",
                        span { "{display_msg}" }
                    }
                }
            },
            _ => {
                let message = match &status {
                    SeedingStatus::Running => "Game Running",
                    SeedingStatus::Stopping => "Stopping...",
                    SeedingStatus::Switching => "Switching...",
                    SeedingStatus::Stopped => "Stopped",
                    _ => "",
                };
                if !message.is_empty() {
                    rsx! {
                        div { class: "shadow-sm p-0 mx-5 w-auto justify-center {status_class}", role: "status",
                            span { "{message}" }
                        }
                    }
                } else {
                    rsx! {}
                }
            },
        }

        // Active settings
        if efficiency_mode && is_active {
            div {
                class: "flex items-center justify-center gap-2 mx-5 mt-1",
                if crate::backend::backup_restore_hll_config::is_efficiency_mode_applied() {
                    span { class: "badge badge-sm badge-success gap-1", "Power Savings Active" }
                } else {
                    span { class: "badge badge-sm gap-1", "Power Savings" }
                }
            }
        }

        // Show "NA" divider when EU is enabled and there are stats or servers
        if eu_enabled && (!servers_stats.is_empty() || !na_servers.is_empty()) {
            div { class: "divider mt-2 mb-0 text-sm", "NA" }
        }

        ServerStatsList {
            stats: servers_stats.clone(),
            server_count: na_servers.len(),
            seeding_threshold: na_seeding_threshold,
            show_real_max: true,
        }

        if eu_enabled {
            div { class: "divider mt-2 mb-0 text-sm", "EU" }
            ServerStatsList {
                stats: eu_servers_stats.clone(),
                server_count: eu_servers.len(),
                seeding_threshold: eu_seeding_threshold,
                is_eu: true,
                show_real_max: true,
            }
        }

        if *SSE_CONNECTED.read() {
            div { class: "flex items-center justify-center gap-2 mt-1 text-xs text-base-content/60",
                span { class: "flex items-center gap-1",
                    span { class: "inline-block w-1.5 h-1.5 rounded-full bg-success" }
                    span { class: "text-success font-medium", "Live" }
                }
                span { "Updated {STALE_INFO.read().seconds_ago}s ago" }
            }
        } else {
            crate::components::reconnect_button::ReconnectButton {}
        }
    }
}
