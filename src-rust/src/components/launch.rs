use dioxus::prelude::*;
use tracing::{error, info};

use crate::components::launch_banner::LaunchBanner;
use crate::state::modal::show_confirm;
use crate::state::seeding::{
    SeedingStatus, IS_SEEDING, SEEDING_INDEX, SEEDING_STATUS,
};
use crate::state::servers::{EU_ENABLED, SERVER_LOAD_ERROR};
use crate::state::session::EFFICIENCY_MODE;

/// Launch page: displays server launch buttons (when idle) or stop controls
/// (when seeding/initializing). A stats banner is shown above the buttons.
#[component]
pub fn Launch() -> Element {
    let status = SEEDING_STATUS.read().clone();
    let is_seeding = *IS_SEEDING.read();
    let eu_enabled = *EU_ENABLED.read();
    let server_load_error = *SERVER_LOAD_ERROR.read();
    let efficiency_mode = *EFFICIENCY_MODE.read();

    // Read backend server lists for the launch buttons
    let na_servers = crate::backend::server::get_servers();
    let eu_servers = crate::backend::server::get_eu_servers();

    // Conditional top padding: reduced when actively seeding/initializing
    let top_padding = match status {
        SeedingStatus::Initializing | SeedingStatus::Seeding => "pt-2",
        _ => "",
    };

    rsx! {
        LaunchBanner {}

        div { class: "flex flex-col w-full justify-center p-6 {top_padding}",
            match status {
                // Idle states: show launch buttons
                SeedingStatus::Idle
                | SeedingStatus::Stopped
                | SeedingStatus::Running
                | SeedingStatus::Error => rsx! {
                    if na_servers.is_empty() {
                        // Server list not loaded yet
                        div { class: "flex flex-col items-center justify-center w-full h-24 gap-2",
                            if server_load_error {
                                span { class: "text-error text-sm", "Failed to load servers" }
                                button {
                                    class: "btn btn-ghost btn-sm underline",
                                    onclick: move |_| {
                                        spawn(async move {
                                            retry_load_servers().await;
                                        });
                                    },
                                    "Tap to retry"
                                }
                            } else {
                                span { class: "loading loading-spinner loading-md" }
                                span { "Loading servers..." }
                            }
                        }
                    } else {
                        // Launch buttons grid
                        div { class: "w-full mb-2 flex flex-wrap",
                            // NA servers
                            for (index, server) in na_servers.iter().enumerate() {
                                {
                                    let short_name = server.short_name.clone();
                                    rsx! {
                                        button {
                                            key: "{server.name}",
                                            class: "btn btn-ghost flex-col gap-1 w-1/2 h-20 text-sm mb-2",
                                            onclick: move |_| {
                                                start_server(index, "na");
                                            },
                                            img { src: asset!("/assets/play_icon.svg"), style: "width: 2rem; height: 2rem; object-fit: contain;", alt: "" }
                                            span { "Launch {short_name}" }
                                        }
                                    }
                                }
                            }

                            // EU servers (if enabled)
                            if eu_enabled {
                                for (index, server) in eu_servers.iter().enumerate() {
                                    {
                                        let short_name = server.short_name.clone();
                                        rsx! {
                                            button {
                                                key: "eu-{server.name}",
                                                class: "btn btn-ghost flex-col gap-1 w-1/2 h-20 text-sm mb-2 opacity-80",
                                                onclick: move |_| {
                                                    start_server(index, "eu");
                                                },
                                                img { src: asset!("/assets/play_icon.svg"), style: "width: 2rem; height: 2rem; object-fit: contain;", alt: "" }
                                                span { "Launch {short_name}" }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },

                // Active seeding/initializing/stopping states: show stop controls
                SeedingStatus::Initializing
                | SeedingStatus::Seeding
                | SeedingStatus::Stopping => rsx! {
                    if efficiency_mode || !is_seeding {
                        // Efficiency mode or launch flow (not seeding): single stop button
                        button {
                            class: "btn btn-ghost flex-col gap-2 w-full h-24 text-xl",
                            onclick: move |_| {
                                stop_game();
                            },
                            img { src: asset!("/assets/stop_icon.svg"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "Stop Game" }
                            span { "Stop Game" }
                        }
                    } else {
                        // Seeding flow: two stop buttons side by side
                        div { class: "w-full flex",
                            button {
                                class: "btn btn-ghost flex-col gap-2 w-1/2 h-24 text-lg",
                                title: "Stop seeding but keep the game running",
                                onclick: move |_| {
                                    stop_seeding_only();
                                },
                                img { src: asset!("/assets/stop_icon.svg"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "Stop Seeding" }
                                span { "Stop Seeding" }
                            }
                            button {
                                class: "btn btn-outline btn-error flex-col gap-2 w-1/2 h-24 text-lg",
                                title: "Stop seeding and close the game",
                                onclick: move |_| {
                                    stop_game();
                                },
                                img { src: asset!("/assets/stop_icon.svg"), style: "width: 2.5rem; height: 2.5rem; object-fit: contain;", alt: "Stop Game" }
                                span { "Stop Game" }
                            }
                        }
                    }
                },

                // Other states (Switching, WaitingForUpdate) — show nothing extra
                _ => rsx! {},
            }
        }
    }
}

/// The core launch logic: set state, call backend.
async fn start_server_inner(server_number: usize, region: &'static str) {
    *IS_SEEDING.write() = false;
    *SEEDING_STATUS.write() = SeedingStatus::Initializing;

    let result = if region == "eu" {
        crate::backend::seeding::start_eu(server_number).await
    } else {
        crate::backend::seeding::start(server_number).await
    };

    match result {
        Ok(()) => {
            *SEEDING_STATUS.write() = SeedingStatus::Running;
        }
        Err(e) => {
            error!("Error starting server: {}", e);
            *SEEDING_STATUS.write() = SeedingStatus::Error;
        }
    }
}

/// Launch a server (no seeding/monitoring). Mirrors the Svelte `startServer` action.
fn start_server(server_number: usize, region: &'static str) {
    let status = SEEDING_STATUS.read().clone();
    if matches!(
        status,
        SeedingStatus::Initializing
            | SeedingStatus::Seeding
            | SeedingStatus::Stopping
            | SeedingStatus::Running
    ) {
        return;
    }

    // If the game is already running, ask for confirmation before relaunching
    let current_game = crate::backend::seeding::current_game();
    if crate::backend::process::is_game_running(current_game) {
        spawn(async move {
            let game = crate::backend::seeding::current_game();
            let confirmed = show_confirm(
                &format!("{} is currently running. Close the game and launch this server?", game.display_name),
                "Game Running",
            )
            .await;

            if !confirmed {
                return;
            }

            crate::backend::seeding::kill_game_process(game);

            // Wait up to 20s for the process to exit
            for _ in 0..40 {
                if !crate::backend::process::is_game_running(game) {
                    break;
                }
                tokio::time::sleep(std::time::Duration::from_millis(500)).await;
            }

            start_server_inner(server_number, region).await;
        });
        return;
    }

    spawn(async move {
        start_server_inner(server_number, region).await;
    });
}

/// Stop seeding and close the game. Mirrors the Svelte `stopSeed` action.
fn stop_game() {
    *SEEDING_STATUS.write() = SeedingStatus::Stopping;
    *SEEDING_INDEX.write() = None;
    *IS_SEEDING.write() = false;

    spawn(async move {
        match crate::backend::seeding::stop_seeding().await {
            Ok(()) => {
                *SEEDING_STATUS.write() = SeedingStatus::Stopped;
            }
            Err(e) => {
                let err_str = format!("{}", e);
                error!("Error stopping seeding: {}", err_str);
                if err_str.contains("HLL closed") {
                    *SEEDING_STATUS.write() = SeedingStatus::Stopped;
                } else {
                    *SEEDING_STATUS.write() = SeedingStatus::Error;
                }
            }
        }
    });
}

/// Stop seeding but keep the game running. Mirrors the Svelte `stopSeedOnly` action.
fn stop_seeding_only() {
    *SEEDING_STATUS.write() = SeedingStatus::Stopping;
    *SEEDING_INDEX.write() = None;
    *IS_SEEDING.write() = false;
    crate::state::seeding::reset_server_switch();

    spawn(async move {
        match crate::backend::seeding::stop_seeding_only().await {
            Ok(()) => {
                *SEEDING_STATUS.write() = SeedingStatus::Stopped;
            }
            Err(e) => {
                error!("Error stopping seeding only: {}", e);
                *SEEDING_STATUS.write() = SeedingStatus::Error;
            }
        }
    });
}

/// Retry loading the server list after an error.
async fn retry_load_servers() {
    if crate::state::cooldown::is_on_cooldown("load_servers") {
        return;
    }
    crate::state::cooldown::set_cooldown("load_servers", 5);

    info!("Retrying server list load");
    // Re-use the same fetch logic from app initialization
    match crate::api::client::get_servers().await {
        Ok(result) => {
            let na_stats: Vec<crate::state::servers::ServerStats> = result
                .hll
                .na
                .iter()
                .map(|s| crate::state::servers::ServerStats {
                    name: s.name.clone(),
                    map: String::new(),
                    players: String::new(),
                    max_player_count: None,
                    offline: false,
                })
                .collect();
            let eu_stats: Vec<crate::state::servers::ServerStats> = result
                .hll
                .eu
                .iter()
                .map(|s| crate::state::servers::ServerStats {
                    name: s.name.clone(),
                    map: String::new(),
                    players: String::new(),
                    max_player_count: None,
                    offline: false,
                })
                .collect();

            {
                let mut servers = write_lock!(crate::backend::server::SERVERS);
                *servers = result
                    .hll
                    .na
                    .into_iter()
                    .map(std::sync::Arc::new)
                    .collect();
            }
            {
                let mut servers = write_lock!(crate::backend::server::EU_SERVERS);
                *servers = result
                    .hll
                    .eu
                    .into_iter()
                    .map(std::sync::Arc::new)
                    .collect();
            }

            *crate::state::servers::SERVERS_STATS.write() = na_stats;
            *crate::state::servers::EU_SERVERS_STATS.write() = eu_stats;
            *crate::state::servers::SERVER_LOAD_ERROR.write() = false;
        }
        Err(e) => {
            error!("Error retrying server load: {}", e);
            let msg = crate::api::client::friendly_error(e.as_ref());
            crate::state::toast::add_toast(
                &format!("Failed to load servers: {}", msg),
                crate::state::toast::ToastType::Error,
                None,
            );
            *crate::state::servers::SERVER_LOAD_ERROR.write() = true;
        }
    }
}
