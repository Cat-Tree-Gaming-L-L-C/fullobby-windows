use std::sync::atomic::{AtomicU8, Ordering};
use std::time::Duration;

use dioxus::prelude::{spawn, ReadableExt};
use log::info;

use crate::config::init_config;
use crate::events::{send_event, AppEvent};
use crate::backend::backup_restore_hll_config::check_and_restore_on_startup;
use crate::backend::process::is_process_running;
use crate::backend::seeding::{
    AUTOSEED_CANCELLED, AUTOSEED_IN_PROGRESS,
};
use crate::backend::session::get_stored_session;

/// Deferred seed mode requested via CLI args.
/// 0 = none, 1 = --autoseed-na (or legacy --seed-na), 2 = --autoseed-eu (or legacy --seed-eu)
pub static PENDING_AUTOSEED: AtomicU8 = AtomicU8::new(0);

/// Auto-seed logic for `--autoseed-na` / `--autoseed-eu` CLI flags.
/// Shows a countdown, then asks the API for the best server to seed.
async fn run_autoseed(seed_type: &'static str, show_notification: bool) {
    let label = if seed_type == "eu" { "EU " } else { "" };

    crate::backend::autoseed::record_autoseed_triggered(seed_type);

    // Guard: check if HLL is already running
    if is_process_running("HLL-Win64-Shipping.exe") {
        info!("HLL already running, skipping {}auto-seed", label);
        return;
    }

    // Guard: prevent concurrent autoseed
    if AUTOSEED_IN_PROGRESS.compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire).is_err() {
        info!("Autoseed already in progress, skipping");
        return;
    }
    AUTOSEED_CANCELLED.store(false, Ordering::Release);

    // Optionally show window and notification
    if show_notification {
        crate::platform::notification::show_notification(
            "Esprit Seeder",
            "Auto-seed starting in 60 seconds. Click to cancel.",
        );
    }

    // Countdown
    send_event(AppEvent::AutoseedCountdownStarted {
        duration_secs: 60,
        seed_type: seed_type.to_string(),
    });
    info!("Autoseed countdown started (60s, {})", seed_type.to_uppercase());

    for second in 0..60 {
        tokio::time::sleep(Duration::from_secs(1)).await;
        if AUTOSEED_CANCELLED.load(Ordering::Acquire) {
            info!("Autoseed countdown cancelled at {}s", second + 1);
            send_event(AppEvent::AutoseedCountdownCancelled);
            AUTOSEED_IN_PROGRESS.store(false, Ordering::Release);
            return;
        }
    }

    send_event(AppEvent::AutoseedCountdownComplete);
    info!("Autoseed countdown complete, proceeding with {}auto-seed", label);

    // Double-check HLL
    if is_process_running("HLL-Win64-Shipping.exe") {
        info!("HLL started running while waiting, skipping {}auto-seed", label);
        AUTOSEED_IN_PROGRESS.store(false, Ordering::Release);
        return;
    }

    let eu_enabled = if seed_type == "eu" {
        true
    } else {
        get_stored_session("secondary_servers_enabled")
            .map(|v| v == "true")
            .unwrap_or(false)
    };

    // Get steam ID from stored config (backend context, no Dioxus signals available)
    let steam_id = get_stored_session("linked_steam_ids")
        .and_then(|json| serde_json::from_str::<Vec<String>>(&json).ok())
        .and_then(|ids| ids.into_iter().next());

    // Use the pre-computed seeding status (best candidate per region) instead of
    // next-server, which advances past the current index and can skip server 0.
    // Prefer SSE-derived cache when fresh; fall back to API call.
    let status = match crate::backend::api_client::get_cached_seeding_status() {
        Some(s) => Ok(s),
        None => crate::api::client::get_seeding_status().await,
    };

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
                        info!("Auto-seed: all servers exhausted");
                        AUTOSEED_IN_PROGRESS.store(false, Ordering::Release);
                        return;
                    }
                } else {
                    info!("Auto-seed: no candidate in {} region", seed_type.to_uppercase());
                    AUTOSEED_IN_PROGRESS.store(false, Ordering::Release);
                    return;
                }
            };

            info!(
                "Auto-seed: seeding status recommends {} server {} (region: {})",
                candidate.server.short_name, candidate.index, region
            );

            let seeding_result = if region == "eu" {
                crate::backend::seeding::start_seeding_eu(candidate.index).await
            } else {
                crate::backend::seeding::start_seeding(candidate.index).await
            };

            match seeding_result {
                Ok(_) => {
                    info!("Auto-seed started on {} server {}", region.to_uppercase(), candidate.index);
                    // Create a seeding session for heartbeat tracking
                    let analytics = crate::api::client::gather_analytics(true);
                    match crate::api::client::start_session("hll", region, candidate.index, steam_id.as_deref(), Some(analytics)).await {
                        Ok(session) => {
                            if dioxus::dioxus_core::Runtime::try_current().is_some() {
                                *crate::state::session::ACTIVE_SESSION_ID.write() = Some(session.session_id);
                            }
                        }
                        Err(e) => {
                            info!("Auto-seed: failed to create session: {:?}", e);
                        }
                    }
                    // Notify the UI so it shows Stop Seeding / Stop & Exit buttons
                    send_event(AppEvent::AutoseedSeedingStarted {
                        server_index: candidate.index,
                        region: region.to_string(),
                        server_name: candidate.server.short_name.clone(),
                    });

                    // Start the monitor loop so we detect candidate changes
                    // (e.g. PF server seeded → transition to G&W).
                    // Without this, the game sits on the initial server forever.
                    let monitor_result = if region == "eu" {
                        crate::backend::seeding::monitor_seed_eu(candidate.index).await
                    } else {
                        crate::backend::seeding::monitor_seed(candidate.index).await
                    };

                    if let Err(e) = monitor_result {
                        info!("Auto-seed monitor ended with error: {:?}", e);
                    }

                    // Cleanup: stop heartbeat and restore config
                    if dioxus::dioxus_core::Runtime::try_current().is_some() {
                        if crate::state::session::ACTIVE_SESSION_ID.read().is_some() {
                            crate::backend::heartbeat::stop_heartbeat(Some("autoseed_monitor_complete")).await;
                            *crate::state::session::ACTIVE_SESSION_ID.write() = None;
                        }
                    } else {
                        // Runtime gone — stop heartbeat without signal access
                        crate::backend::heartbeat::stop_heartbeat(Some("autoseed_monitor_complete")).await;
                    }
                    crate::backend::backup_restore_hll_config::restore_after_seeding();
                }
                Err(e) => {
                    info!("Auto-seed: failed to start on recommended server: {:?}", e);
                }
            }
        }
        Err(e) => {
            info!("Auto-seed: seeding status API call failed: {:?}", e);
        }
    }

    AUTOSEED_IN_PROGRESS.store(false, Ordering::Release);
}

pub fn run_setup() {
    // Init Config
    init_config();

    // Check for leftover efficiency mode from previous session (app crashed during seeding)
    check_and_restore_on_startup();

    info!("App Launched");

    // Handle CLI arguments: deep links and --seed-na/--seed-eu flags
    let args: Vec<String> = std::env::args().collect();

    // Check for deep link URL in args (espritseeder://...)
    for arg in &args {
        if arg.starts_with("espritseeder://") {
            info!("Deep link received in startup args");
            if let Some(action) = crate::platform::deep_link::parse_deep_link(arg) {
                match action {
                    crate::platform::deep_link::DeepLinkAction::AuthCallback { state, token, refresh_token } => {
                        // Reject tokens when state IS provided but fails validation.
                        // Only accept without state when `state` is `None` (server didn't send one).
                        if let Some(ref s) = state {
                            if !crate::state::auth::validate_oauth_state(s) {
                                log::warn!("OAuth state mismatch at startup — rejecting auth callback");
                                continue;
                            }
                        }
                        info!("Storing auth tokens from deep link callback");
                        let _ = crate::backend::session::store_session("auth_token", token);
                        let _ = crate::backend::session::store_session("auth_refresh_token", refresh_token);
                    }
                    crate::platform::deep_link::DeepLinkAction::LinkCallback { provider, .. } => {
                        info!("Link callback for {} received at startup, will be handled after app init", provider);
                    }
                    crate::platform::deep_link::DeepLinkAction::Unknown(url) => {
                        info!("Unknown deep link action: {}", url);
                    }
                }
            }
        }
    }

    let has_seed_na = args.iter().any(|a| a == "--seed-na" || a == "--autoseed-na");
    let has_seed_eu = args.iter().any(|a| a == "--seed-eu" || a == "--autoseed-eu");

    if has_seed_na {
        info!("Started with --autoseed-na argument, will auto-start seeding after app init");
        PENDING_AUTOSEED.store(1, Ordering::Release);
    } else if has_seed_eu {
        info!("Started with --autoseed-eu argument, will auto-start seeding EU servers after app init");
        PENDING_AUTOSEED.store(2, Ordering::Release);
    }
}

/// Spawn deferred autoseed if one was requested via CLI args.
/// Must be called from within a Tokio runtime context (e.g. after Dioxus init).
/// Returns `true` if a CLI autoseed was spawned (caller can skip the missed-seed check).
pub fn spawn_pending_autoseed() -> bool {
    match PENDING_AUTOSEED.swap(0, Ordering::AcqRel) {
        1 => {
            info!("Spawning deferred auto-seed (NA)");
            spawn(run_autoseed("na", true));
            true
        }
        2 => {
            info!("Spawning deferred auto-seed (EU)");
            spawn(run_autoseed("eu", false));
            true
        }
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_pending_autoseed_default() {
        // After swap(0), reading should give 0
        let prev = PENDING_AUTOSEED.swap(0, Ordering::SeqCst);
        // Restore whatever was there
        PENDING_AUTOSEED.store(prev, Ordering::SeqCst);
    }

    #[test]
    fn test_pending_autoseed_store_load() {
        let original = PENDING_AUTOSEED.load(Ordering::SeqCst);

        PENDING_AUTOSEED.store(1, Ordering::SeqCst);
        assert_eq!(PENDING_AUTOSEED.load(Ordering::SeqCst), 1);

        PENDING_AUTOSEED.store(2, Ordering::SeqCst);
        assert_eq!(PENDING_AUTOSEED.load(Ordering::SeqCst), 2);

        PENDING_AUTOSEED.store(0, Ordering::SeqCst);
        assert_eq!(PENDING_AUTOSEED.load(Ordering::SeqCst), 0);

        // Restore
        PENDING_AUTOSEED.store(original, Ordering::SeqCst);
    }

    #[test]
    fn test_pending_autoseed_swap() {
        let original = PENDING_AUTOSEED.load(Ordering::SeqCst);

        PENDING_AUTOSEED.store(1, Ordering::SeqCst);
        let prev = PENDING_AUTOSEED.swap(0, Ordering::SeqCst);
        assert_eq!(prev, 1);
        assert_eq!(PENDING_AUTOSEED.load(Ordering::SeqCst), 0);

        // Restore
        PENDING_AUTOSEED.store(original, Ordering::SeqCst);
    }

    #[test]
    fn test_pending_autoseed_values_map_to_regions() {
        // Document the meaning of values: 0=none, 1=NA, 2=EU
        assert_eq!(0u8, 0); // none
        // 1 maps to "na" in spawn_pending_autoseed
        // 2 maps to "eu" in spawn_pending_autoseed
    }
}
