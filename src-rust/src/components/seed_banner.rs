use dioxus::prelude::*;

use crate::state::events::{SPLASH_BYPASS_ACTIVE, SPLASH_BYPASS_REMAINING};
use crate::state::seeding::{
    SeedingRegion, SeedingStatus, SEEDING_ERROR_MESSAGE, SEEDING_INDEX, SEEDING_REGION,
    SEEDING_STATUS,
};
use crate::state::servers::{EU_ENABLED, EU_SERVERS_STATS, SERVERS_STATS};
use crate::state::session::EFFICIENCY_MODE;

/// Configuration for each seeding status: CSS class + default message.
struct StatusConfig {
    class: &'static str,
    message: &'static str,
}

fn status_config(status: &SeedingStatus) -> Option<StatusConfig> {
    match status {
        SeedingStatus::Initializing => Some(StatusConfig {
            class: "alert",
            message: "Initializing",
        }),
        SeedingStatus::Running => Some(StatusConfig {
            class: "alert alert-success",
            message: "Game Running",
        }),
        SeedingStatus::Seeding => Some(StatusConfig {
            class: "alert alert-success",
            message: "Seeding",
        }),
        SeedingStatus::Stopping => Some(StatusConfig {
            class: "alert",
            message: "Stopping...",
        }),
        SeedingStatus::Switching => Some(StatusConfig {
            class: "alert",
            message: "Switching...",
        }),
        SeedingStatus::Stopped => Some(StatusConfig {
            class: "alert",
            message: "Stopped",
        }),
        SeedingStatus::WaitingForUpdate => Some(StatusConfig {
            class: "alert",
            message: "Waiting for game update...",
        }),
        SeedingStatus::Error => Some(StatusConfig {
            class: "alert alert-error",
            message: "",  // handled separately in render
        }),
        SeedingStatus::Idle => None,
    }
}

#[component]
pub fn SeedBanner() -> Element {
    let status = SEEDING_STATUS.read().clone();
    let seeding_index = *SEEDING_INDEX.read();
    let seeding_region = SEEDING_REGION.read().clone();
    let splash_bypass_active = *SPLASH_BYPASS_ACTIVE.read();
    let splash_bypass_remaining = *SPLASH_BYPASS_REMAINING.read();
    let efficiency_mode = *EFFICIENCY_MODE.read();
    let eu_enabled = *EU_ENABLED.read();
    let servers_stats = SERVERS_STATS.read();
    let eu_servers_stats = EU_SERVERS_STATS.read();

    let config = status_config(&status);

    let is_active = matches!(
        status,
        SeedingStatus::Initializing
            | SeedingStatus::Seeding
            | SeedingStatus::Running
            | SeedingStatus::Switching
            | SeedingStatus::WaitingForUpdate
    );

    // Derive the seeding server name from the current index and stats
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

    // Compute server counts and seeding thresholds for ServerStatsList props
    let na_server_count = servers_stats.len();
    let na_servers = crate::backend::server::get_servers();
    let eu_servers = crate::backend::server::get_eu_servers();
    let na_seeding_threshold: Vec<i32> = na_servers.iter().map(|s| s.seeding_threshold).collect();
    let eu_seeding_threshold: Vec<i32> = eu_servers.iter().map(|s| s.seeding_threshold).collect();
    let eu_server_count = eu_servers_stats.len();

    let na_stats_clone = servers_stats.clone();
    let eu_stats_clone = eu_servers_stats.clone();

    drop(servers_stats);
    drop(eu_servers_stats);

    let phase_suffix = if seeding_region == SeedingRegion::Eu {
        " (EU)"
    } else {
        ""
    };
    let phase_suffix_short = if seeding_region == SeedingRegion::Eu {
        " EU"
    } else {
        ""
    };

    rsx! {
        // Status banner
        if let Some(cfg) = &config {
            div {
                role: "status",
                aria_live: "polite",
                class: format!("shadow-sm p-0 mx-5 w-auto justify-center {}", cfg.class),
                match (&status, seeding_index) {
                    (SeedingStatus::Seeding, Some(_)) => rsx! {
                        span { "Seeding {server_name}{phase_suffix}" }
                    },
                    (SeedingStatus::Initializing, Some(_)) => rsx! {
                        span { "Initializing {server_name}{phase_suffix}" }
                    },
                    (SeedingStatus::WaitingForUpdate, Some(_)) => rsx! {
                        span { "Waiting for game update ({server_name}{phase_suffix_short})" }
                    },
                    (SeedingStatus::Error, _) => {
                        let err_msg = SEEDING_ERROR_MESSAGE.read();
                        let display_msg = if err_msg.is_empty() {
                            "An error occurred. Please try again.".to_string()
                        } else {
                            err_msg.clone()
                        };
                        rsx! { span { "{display_msg}" } }
                    },
                    _ => rsx! {
                        span { "{cfg.message}" }
                    },
                }
            }
        }

        // Splash bypass banner
        if splash_bypass_active {
            div {
                role: "status",
                aria_live: "polite",
                class: "alert shadow-sm p-2 mx-5 mt-1 w-auto text-sm text-center justify-center",
                span {
                    {format!(
                        "Skipping intro ({}:{:02} remaining)",
                        splash_bypass_remaining / 60,
                        splash_bypass_remaining % 60
                    )}
                }
            }
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

        // DEV build badge
        if cfg!(debug_assertions) {
            div { class: "badge badge-warning badge-sm mx-auto mt-1", "DEV BUILD" }
        }

        // NA divider (shown when EU is enabled and there are NA servers/stats)
        if eu_enabled && na_server_count > 0 {
            div { class: "divider mt-2 mb-0 text-sm", "NA" }
        }

        // NA server stats list
        crate::components::server_stats_list::ServerStatsList {
            stats: na_stats_clone,
            server_count: na_server_count,
            seeding_threshold: na_seeding_threshold,
        }

        // EU section
        if eu_enabled {
            div { class: "divider mt-2 mb-0 text-sm", "EU" }
            crate::components::server_stats_list::ServerStatsList {
                stats: eu_stats_clone,
                server_count: eu_server_count,
                seeding_threshold: eu_seeding_threshold,
                is_eu: true,
            }
        }

        // Live status or reconnect button
        if *crate::state::servers::SSE_CONNECTED.read() {
            div { class: "flex items-center justify-center gap-2 mt-1 text-xs text-base-content/60",
                span { class: "flex items-center gap-1",
                    span { class: "inline-block w-1.5 h-1.5 rounded-full bg-success" }
                    span { class: "text-success font-medium", "Live" }
                }
                span { "Updated {crate::state::stale_timer::STALE_INFO.read().seconds_ago}s ago" }
            }
        } else {
            crate::components::reconnect_button::ReconnectButton {}
        }
    }
}
