use dioxus::prelude::*;
use tracing::{info, warn};

use crate::events::{subscribe, AppEvent};
use crate::start_countdown_signal;
use crate::state::countdowns::{stop_countdown, stop_all_countdowns};
use crate::state::seeding::*;
use crate::state::toast::{add_toast, ToastType};

// Splash bypass state
pub static SPLASH_BYPASS_ACTIVE: GlobalSignal<bool> = Signal::global(|| false);
pub static SPLASH_BYPASS_REMAINING: GlobalSignal<u64> = Signal::global(|| 0);

// Autoseed countdown state
pub static AUTOSEED_COUNTDOWN_ACTIVE: GlobalSignal<bool> = Signal::global(|| false);
pub static AUTOSEED_COUNTDOWN_REMAINING: GlobalSignal<u64> = Signal::global(|| 0);
pub static AUTOSEED_COUNTDOWN_TYPE: GlobalSignal<String> = Signal::global(String::new);

fn reset_autoseed_countdown() {
    *AUTOSEED_COUNTDOWN_ACTIVE.write() = false;
    *AUTOSEED_COUNTDOWN_REMAINING.write() = 0;
    *AUTOSEED_COUNTDOWN_TYPE.write() = String::new();
    stop_countdown("autoseed");
}

/// Cancel the autoseed countdown (calls backend)
pub fn cancel_autoseed() {
    info!("Cancelling autoseed countdown");
    crate::backend::seeding::cancel_autoseed();
    reset_autoseed_countdown();
}

/// Start the event listener coroutine — subscribes to the broadcast channel
/// and updates GlobalSignals accordingly.
pub fn setup_event_listeners() {
    // Subscribe synchronously before spawning so the receiver exists immediately.
    // This prevents a race where send_event() fires before the async task starts
    // (e.g. check_missed_autoseed during initialize_app).
    let mut rx = subscribe();

    spawn(async move {

        loop {
            // If the Dioxus runtime has been torn down (app closing / restart),
            // stop processing events to avoid panicking on GlobalSignal access.
            if dioxus::dioxus_core::Runtime::try_current().is_none() {
                tracing::warn!("Event listener: Dioxus runtime gone, stopping");
                break;
            }

            match rx.recv().await {
                Ok(event) => handle_event(event),
                Err(tokio::sync::broadcast::error::RecvError::Lagged(n)) => {
                    info!("Event listener lagged by {} events", n);
                }
                Err(tokio::sync::broadcast::error::RecvError::Closed) => {
                    info!("Event channel closed");
                    break;
                }
            }
        }
    });
}

fn handle_event(event: AppEvent) {
    match event {
        AppEvent::SplashBypassStarted { duration_secs } => {
            *SPLASH_BYPASS_ACTIVE.write() = true;
            *SPLASH_BYPASS_REMAINING.write() = duration_secs;
            start_countdown_signal!("splash-bypass", SPLASH_BYPASS_REMAINING, 0, None);
        }

        AppEvent::SplashBypassComplete => {
            *SPLASH_BYPASS_ACTIVE.write() = false;
            *SPLASH_BYPASS_REMAINING.write() = 0;
            stop_countdown("splash-bypass");
        }

        AppEvent::SplashBypassTimeout { reason } => {
            info!("Splash bypass timed out: {}", reason);
            *SPLASH_BYPASS_ACTIVE.write() = false;
            *SPLASH_BYPASS_REMAINING.write() = 0;
            stop_countdown("splash-bypass");
            add_toast(
                &format!("Splash bypass failed: {}", reason),
                ToastType::Error,
                Some(5000),
            );
        }

        AppEvent::HllClosed => {
            let status = SEEDING_STATUS.read().clone();
            if matches!(
                status,
                SeedingStatus::Initializing | SeedingStatus::Switching | SeedingStatus::WaitingForUpdate
            ) {
                info!("HLL closed event during initialization/switching/waiting, ignoring");
                return;
            }
            info!("HLL was closed, stopping seeding");
            reset_server_switch();
            *SEEDING_STATUS.write() = SeedingStatus::Stopped;
            *SEEDING_INDEX.write() = None;
            *IS_SEEDING.write() = false;
        }

        AppEvent::AutoseedCountdownStarted {
            duration_secs,
            seed_type,
        } => {
            info!(
                "Autoseed countdown started: {}s, type: {}",
                duration_secs, seed_type
            );
            stop_countdown("autoseed");
            *AUTOSEED_COUNTDOWN_ACTIVE.write() = true;
            *AUTOSEED_COUNTDOWN_REMAINING.write() = duration_secs;
            *AUTOSEED_COUNTDOWN_TYPE.write() = seed_type;
            start_countdown_signal!("autoseed", AUTOSEED_COUNTDOWN_REMAINING, 1, None);
        }

        AppEvent::AutoseedCountdownComplete => {
            info!("Autoseed countdown complete");
            reset_autoseed_countdown();
        }

        AppEvent::AutoseedCountdownCancelled => {
            info!("Autoseed countdown cancelled");
            reset_autoseed_countdown();
        }

        AppEvent::ServerSwitchPending {
            countdown_secs,
            server_name,
            reason,
        } => {
            info!(
                "Server switch pending: {}s, server: {}, reason: {}",
                countdown_secs, server_name, reason
            );
            reset_server_switch();
            *SERVER_SWITCH_ACTIVE.write() = true;
            *SERVER_SWITCH_COUNTDOWN.write() = countdown_secs;
            *SERVER_SWITCH_SERVER_NAME.write() = server_name;
            *SERVER_SWITCH_REASON.write() = reason;
            start_countdown_signal!("server-switch", SERVER_SWITCH_COUNTDOWN, 0, None);
        }

        AppEvent::ServerSwitchSnoozed { snooze_secs } => {
            info!("Server switch snoozed: {}s", snooze_secs);
            stop_countdown("server-switch");
            *SERVER_SWITCH_SNOOZED.write() = true;
            *SERVER_SWITCH_SNOOZE_REMAINING.write() = snooze_secs;
            start_countdown_signal!("server-snooze", SERVER_SWITCH_SNOOZE_REMAINING, 0, None);
        }

        AppEvent::ServerSwitchExecuting => {
            info!("Server switch executing");
            reset_server_switch();
            *SEEDING_STATUS.write() = SeedingStatus::Switching;
        }

        AppEvent::ServerSwitchCancelled => {
            info!("Server switch cancelled");
            reset_server_switch();
        }

        AppEvent::AutoseedSeedingStarted {
            server_index,
            region,
            server_name,
        } => {
            info!(
                "Auto-seed monitoring: {} (index: {}, region: {})",
                server_name, server_index, region
            );
            *SEEDING_REGION.write() = if region == "eu" {
                SeedingRegion::Eu
            } else {
                SeedingRegion::Na
            };
            *SEEDING_INDEX.write() = Some(server_index);
            *IS_SEEDING.write() = true;
            *SEEDING_STATUS.write() = SeedingStatus::Seeding;
        }

        AppEvent::AutoseedServerRestartFailed {
            server_name,
            reason,
        } => {
            info!("Autoseed restart failed for {}: {}", server_name, reason);
            add_toast(
                &format!("{} restart failed, trying next server", server_name),
                ToastType::Info,
                Some(5000),
            );
        }

        AppEvent::SeedingUpdateWaiting {
            server_index,
            region,
        } => {
            info!(
                "Launch watcher active: waiting for game update (server: {}, region: {})",
                server_index, region
            );
            *SEEDING_STATUS.write() = SeedingStatus::WaitingForUpdate;
            *IS_SEEDING.write() = true;
            *SEEDING_INDEX.write() = Some(server_index);
            *SEEDING_REGION.write() = if region == "eu" {
                SeedingRegion::Eu
            } else {
                SeedingRegion::Na
            };
        }

        AppEvent::SeedingUpdateStarted {
            server_index,
            region,
        } => {
            info!(
                "Launch watcher restarted seeding (server: {}, region: {})",
                server_index, region
            );
            *SEEDING_STATUS.write() = SeedingStatus::Seeding;
            *IS_SEEDING.write() = true;
            *SEEDING_INDEX.write() = Some(server_index);
            *SEEDING_REGION.write() = if region == "eu" {
                SeedingRegion::Eu
            } else {
                SeedingRegion::Na
            };
        }

        AppEvent::SeedingUpdateTimeout => {
            info!("Game update watcher timed out");
            *SEEDING_STATUS.write() = SeedingStatus::Error;
            *SEEDING_INDEX.write() = None;
            *IS_SEEDING.write() = false;
        }

        AppEvent::SingleInstance { args } => {
            info!("Single-instance event received with args: {:?}", args);
            // Single-instance auto-seed handling is done in a separate coroutine
            // because it involves async operations (server loading waits, countdown starts)
            handle_single_instance_autoseed(args);
        }

        AppEvent::PlayerNameReset => {
            info!("Player name reset event");
            *crate::state::session::PLAYER_NAME.write() = String::new();
        }
    }
}

fn handle_single_instance_autoseed(args: Vec<String>) {
    // Check for deep link URLs first
    for arg in &args {
        if arg.starts_with("espritseeder://") {
            info!("Deep link received via single instance");
            if let Some(action) = crate::platform::deep_link::parse_deep_link(arg) {
                match action {
                    crate::platform::deep_link::DeepLinkAction::AuthCallback { state, token, refresh_token } => {
                        // Validate OAuth state (CSRF protection)
                        if let Some(ref s) = state {
                            if !crate::state::auth::validate_oauth_state(s) {
                                warn!("OAuth state mismatch — rejecting auth callback");
                                return;
                            }
                        }

                        let token_clone = token.clone();
                        let refresh_clone = refresh_token.clone();
                        spawn(async move {
                            // Store tokens
                            crate::api::client::set_tokens(&token_clone, &refresh_clone);
                            let _ = crate::backend::session::store_session("auth_token", token_clone);
                            let _ = crate::backend::session::store_session("auth_refresh_token", refresh_clone);

                            // Fetch user info
                            match crate::api::client::get_me().await {
                                Ok(me) => {
                                    *crate::state::auth::USER.write() = Some(crate::app::convert_user_info(me));
                                    *crate::state::auth::IS_LOGGED_IN.write() = true;
                                    *crate::state::auth::AUTH_METHOD.write() = crate::state::auth::AuthMethod::Jwt;

                                    // Refresh linked data after login
                                    crate::state::session::refresh_steam_ids_from_api().await;
                                    crate::state::session::refresh_linked_providers().await;

                                    // Advance onboarding: sign-in (0) → link (1), link (1) → done (2)
                                    let step = *crate::state::auth::ONBOARDING_STEP.read();
                                    if step <= 1 {
                                        *crate::state::auth::ONBOARDING_STEP.write() = step + 1;
                                    }

                                    info!("Auth updated via deep link callback");
                                }
                                Err(e) => {
                                    info!("Failed to fetch user after deep link auth: {}", e);
                                }
                            }
                        });
                        return;
                    }
                    crate::platform::deep_link::DeepLinkAction::LinkCallback { provider, provider_id } => {
                        info!("Link callback received: provider={}, provider_id={}", provider, provider_id);
                        let provider_display = provider.clone();
                        spawn(async move {
                            let was_guest = *crate::state::auth::IS_GUEST.read();

                            // Refresh user info to pick up newly linked provider
                            match crate::api::client::get_me().await {
                                Ok(me) => {
                                    let is_now_guest = me.auth_provider == crate::api::types::AuthProvider::Guest;
                                    *crate::state::auth::USER.write() = Some(crate::app::convert_user_info(me.clone()));

                                    // Detect guest-to-permanent upgrade
                                    if was_guest && !is_now_guest {
                                        *crate::state::auth::IS_GUEST.write() = false;
                                        let provider = crate::app::convert_auth_provider(me.auth_provider.clone());
                                        *crate::state::auth::AUTH_PROVIDER.write() = Some(provider);
                                        let _ = crate::backend::session::store_session("guest_mode", "false".to_string());
                                        let _ = crate::backend::session::store_session("auth_provider", me.auth_provider.to_string());
                                    }
                                }
                                Err(e) => {
                                    info!("Failed to fetch user after link callback: {}", e);
                                }
                            }

                            // Refresh linked data
                            crate::state::session::refresh_steam_ids_from_api().await;
                            crate::state::session::refresh_linked_providers().await;

                            // Toast success (skip during onboarding — the UI already shows feedback)
                            if *crate::state::auth::ONBOARDING_COMPLETE.read() {
                                let label = match provider_display.as_str() {
                                    "steam" => "Steam",
                                    "discord" => "Discord",
                                    _ => &provider_display,
                                };
                                let msg = if was_guest {
                                    format!("{} linked — your account is now permanent!", label)
                                } else {
                                    format!("{} account linked successfully", label)
                                };
                                add_toast(&msg, ToastType::Success, None);
                            }
                        });
                        return;
                    }
                    crate::platform::deep_link::DeepLinkAction::Unknown(url) => {
                        info!("Unknown deep link action from single instance: {}", url);
                    }
                }
            }
            return;
        }
    }

    let has_na = args.iter().any(|a| a == "--seed-na" || a == "--autoseed-na");
    let has_eu = args.iter().any(|a| a == "--seed-eu" || a == "--autoseed-eu");

    if !has_na && !has_eu {
        return;
    }

    let region = if has_eu { "eu" } else { "na" };
    crate::backend::autoseed::record_autoseed_triggered(region);

    spawn(async move {
        // Guard: bail out if Dioxus runtime is gone (app closing / restart)
        if dioxus::dioxus_core::Runtime::try_current().is_none() {
            info!("Single-instance autoseed: Dioxus runtime gone, skipping");
            return;
        }

        let incoming_type = if has_eu {
            SeedingRegion::Eu
        } else {
            SeedingRegion::Na
        };

        let status = SEEDING_STATUS.read().clone();

        // Skip if game is actively running or transitioning
        if matches!(
            status,
            SeedingStatus::Running | SeedingStatus::Stopping | SeedingStatus::Switching
        ) {
            info!("Game running or transitioning, skipping single-instance auto-seed");
            return;
        }

        let is_seeding_now = matches!(status, SeedingStatus::Initializing | SeedingStatus::Seeding);
        let countdown_active = *AUTOSEED_COUNTDOWN_ACTIVE.read();
        let is_busy = countdown_active || is_seeding_now;

        if is_busy {
            let current_type = if countdown_active {
                let t = AUTOSEED_COUNTDOWN_TYPE.read().clone();
                if t == "eu" { SeedingRegion::Eu } else { SeedingRegion::Na }
            } else {
                SEEDING_REGION.read().clone()
            };

            if current_type == incoming_type {
                info!("Same-type auto-seed ({:?}) already active, skipping", incoming_type);
                return;
            }

            info!("Switching from {:?} to {:?}", current_type, incoming_type);
            *SEEDING_STATUS.write() = SeedingStatus::Switching;

            if countdown_active {
                cancel_autoseed();
            }

            if is_seeding_now {
                // stop_seed is in the seeding action module — will be wired later
                crate::backend::seeding::stop_seeding().await.ok();
                tokio::time::sleep(std::time::Duration::from_secs(2)).await;
            }
        }

        if !is_busy {
            if crate::backend::process::is_process_running("HLL-Win64-Shipping.exe") {
                info!("HLL already running, skipping single-instance auto-seed");
                return;
            }
        }

        // Wait for servers to load
        let loaded = wait_for_servers(&incoming_type).await;
        if !loaded {
            return;
        }

        // Start frontend countdown (60s)
        *AUTOSEED_COUNTDOWN_ACTIVE.write() = true;
        *AUTOSEED_COUNTDOWN_REMAINING.write() = 60;
        *AUTOSEED_COUNTDOWN_TYPE.write() = incoming_type.as_str().to_string();
        start_countdown_signal!("autoseed", AUTOSEED_COUNTDOWN_REMAINING, 0, None);

        // Wait for countdown to finish
        loop {
            tokio::time::sleep(std::time::Duration::from_secs(1)).await;
            if *AUTOSEED_COUNTDOWN_REMAINING.read() == 0 || !*AUTOSEED_COUNTDOWN_ACTIVE.read() {
                break;
            }
        }

        if !*AUTOSEED_COUNTDOWN_ACTIVE.read() {
            // Countdown was cancelled
            return;
        }

        reset_autoseed_countdown();

        // Double-check HLL hasn't started during the countdown
        if crate::backend::process::is_process_running("HLL-Win64-Shipping.exe") {
            info!("HLL started running during countdown, skipping single-instance auto-seed");
            return;
        }

        let seed_type = incoming_type.as_str();
        info!("Single-instance autoseed starting for {:?}", incoming_type);

        let eu_enabled = if seed_type == "eu" {
            true
        } else {
            crate::backend::session::get_stored_session("secondary_servers_enabled")
                .map(|v| v == "true")
                .unwrap_or(false)
        };

        let steam_id = crate::backend::session::get_stored_session("linked_steam_ids")
            .and_then(|json| serde_json::from_str::<Vec<String>>(&json).ok())
            .and_then(|ids| ids.into_iter().next());

        // Use the pre-computed seeding status (best candidate per region) instead of
        // next-server, which advances past the current index and can skip server 0.
        let status = crate::api::client::get_seeding_status().await;

        match status {
            Ok(status) => {
                let (candidate, region) = {
                    let (primary, primary_region) = if seed_type == "eu" {
                        (&status.hll.eu, "eu")
                    } else {
                        (&status.hll.na, "na")
                    };
                    let (fallback, fallback_region) = if seed_type == "eu" {
                        (&status.hll.na, "na")
                    } else {
                        (&status.hll.eu, "eu")
                    };

                    if let Some(c) = primary {
                        (c.clone(), primary_region)
                    } else if eu_enabled {
                        if let Some(c) = fallback {
                            (c.clone(), fallback_region)
                        } else {
                            info!("Single-instance auto-seed: all servers exhausted");
                            return;
                        }
                    } else {
                        info!("Single-instance auto-seed: no candidate in {} region", seed_type.to_uppercase());
                        return;
                    }
                };

                info!(
                    "Single-instance auto-seed: seeding status recommends {} server {} (region: {})",
                    candidate.server.short_name, candidate.index, region
                );

                let seeding_result = if region == "eu" {
                    crate::backend::seeding::start_seeding_eu(candidate.index).await
                } else {
                    crate::backend::seeding::start_seeding(candidate.index).await
                };

                match seeding_result {
                    Ok(_) => {
                        info!(
                            "Single-instance auto-seed started on {} server {}",
                            region.to_uppercase(),
                            candidate.index
                        );
                        // Create a seeding session for heartbeat tracking
                        let analytics = crate::api::client::gather_analytics(true);
                        match crate::api::client::start_session("hll", region, candidate.index, steam_id.as_deref(), Some(analytics)).await {
                            Ok(session) => {
                                *crate::state::session::ACTIVE_SESSION_ID.write() = Some(session.session_id);
                            }
                            Err(e) => {
                                info!("Single-instance auto-seed: failed to create session: {:?}", e);
                            }
                        }
                        // Update UI state so Stop Seeding / Stop & Exit buttons appear
                        *SEEDING_REGION.write() = if region == "eu" {
                            SeedingRegion::Eu
                        } else {
                            SeedingRegion::Na
                        };
                        *SEEDING_INDEX.write() = Some(candidate.index);
                        *IS_SEEDING.write() = true;
                        *SEEDING_STATUS.write() = SeedingStatus::Seeding;
                    }
                    Err(e) => {
                        info!("Single-instance auto-seed: failed to start: {:?}", e);
                    }
                }
            }
            Err(e) => {
                info!("Single-instance auto-seed: seeding status API call failed: {:?}", e);
            }
        }
    });
}

async fn wait_for_servers(region: &SeedingRegion) -> bool {
    let check_fn = match region {
        SeedingRegion::Na => || !crate::state::servers::SERVERS_STATS.read().is_empty(),
        SeedingRegion::Eu => || !crate::state::servers::EU_SERVERS_STATS.read().is_empty(),
    };

    if check_fn() {
        return true;
    }

    info!(
        "Waiting for {:?} servers to load for single-instance auto-seed...",
        region
    );

    for attempt in 0..30 {
        tokio::time::sleep(std::time::Duration::from_secs(2)).await;
        if check_fn() {
            info!("{:?} servers loaded after {}s", region, (attempt + 1) * 2);
            return true;
        }
    }

    info!(
        "Timeout waiting for {:?} servers to load for single-instance auto-seed",
        region
    );
    false
}

/// Clean up event listeners and reset state
pub fn cleanup_event_listeners() {
    stop_all_countdowns();
    reset_server_switch();
}
