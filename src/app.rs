use std::sync::atomic::Ordering;

use dioxus::prelude::*;

use crate::components;
use crate::state;

/// Active page index (signal-based tab navigation, not router)
pub static CURRENT_PAGE: GlobalSignal<usize> = Signal::global(|| 0);

#[component]
pub fn App() -> Element {
    // Initialize on first render
    use_effect(move || {
        spawn(async move {
            initialize_app().await;
        });
    });

    // Server stats polling fallback — reacts quickly to SSE disconnects
    use_effect(move || {
        spawn(async move {
            loop {
                // If the Dioxus runtime has been torn down (app closing / restart),
                // stop polling to avoid panicking on GlobalSignal access or document::eval.
                if dioxus::dioxus_core::Runtime::try_current().is_none() {
                    tracing::warn!("Polling loop: Dioxus runtime gone, stopping");
                    break;
                }

                tokio::select! {
                    // Normal 60s timer — includes wake detection + SSE reconnect kick
                    _ = async {
                        let before = std::time::Instant::now();
                        tokio::time::sleep(std::time::Duration::from_secs(60)).await;
                        let elapsed = before.elapsed();

                        let after_wake = elapsed > std::time::Duration::from_secs(180);
                        if after_wake {
                            // NOTE: After wake, the webview may log "Os { code: 10053, ...}"
                            // (WSAECONNABORTED). This is benign — the old SSE TCP connection
                            // was dropped during sleep and the reconnect below handles it.
                            tracing::info!(
                                "System wake detected (sleep took {:.0}s instead of 60s), waiting 3s for network",
                                elapsed.as_secs_f64()
                            );
                            tokio::time::sleep(std::time::Duration::from_secs(3)).await;
                            // Only request SSE reconnect if it hasn't already reconnected
                            if !*state::servers::SSE_CONNECTED.read() {
                                crate::api::sse::request_reconnect();
                            }

                            // Webview health check: after wake the IPC pipe can break
                            // (os error 10053), leaving the UI frozen.  Ping the webview
                            // with a trivial JS eval — if it doesn't respond within 10s,
                            // auto-restart the app so the user isn't stuck.
                            let mut eval = document::eval("dioxus.send(1)");
                            match tokio::time::timeout(
                                std::time::Duration::from_secs(10),
                                eval.recv::<serde_json::Value>(),
                            ).await {
                                Ok(Ok(_)) => {
                                    tracing::info!("Post-wake webview health check passed");
                                }
                                other => {
                                    tracing::error!(
                                        "Post-wake webview health check failed ({:?}), restarting app",
                                        other
                                    );
                                    crate::platform::restart::restart_app();
                                }
                            }
                        }

                        // Check for missed autoseeds every cycle (covers Modern Standby, Task Scheduler failures)
                        let missed = crate::backend::autoseed::check_missed_autoseed(after_wake).await;
                        if !missed.is_empty() {
                            tracing::info!("Triggered missed autoseed for: {:?}", missed);
                        }
                    } => {}
                    // SSE disconnected — wait 2s for reconnect attempt, then poll
                    _ = crate::api::sse::poll_notified() => {
                        tokio::time::sleep(std::time::Duration::from_secs(2)).await;
                    }
                }

                // Skip polling when SSE is providing live updates
                if *state::servers::SSE_CONNECTED.read() {
                    continue;
                }

                refresh_server_stats().await;
            }
        });
    });

    // Periodic server list refresh
    use_effect(move || {
        spawn(async move {
            // Initial delay — servers were just fetched in initialize_app()
            tokio::time::sleep(std::time::Duration::from_secs(
                crate::backend::server::SERVER_REFRESH_INTERVAL_SECS,
            ))
            .await;

            let mut failures: u32 = 0;
            loop {
                if dioxus::dioxus_core::Runtime::try_current().is_none() {
                    tracing::warn!("Server refresh loop: Dioxus runtime gone, stopping");
                    break;
                }

                match crate::api::client::get_servers().await {
                    Ok(resp) => {
                        apply_server_list(resp);
                        failures = 0;
                    }
                    Err(e) => {
                        tracing::warn!("Server list refresh failed: {}", e);
                        failures = failures.saturating_add(1);
                    }
                }

                let delay = if failures == 0 {
                    crate::backend::server::SERVER_REFRESH_INTERVAL_SECS
                } else {
                    let backoff = crate::backend::server::SERVER_REFRESH_MIN_INTERVAL_SECS
                        * (1u64 << (failures - 1).min(10));
                    backoff.min(crate::backend::server::SERVER_REFRESH_MAX_BACKOFF_SECS)
                };
                let before = std::time::Instant::now();
                tokio::time::sleep(std::time::Duration::from_secs(delay)).await;
                let elapsed = before.elapsed();
                if elapsed > std::time::Duration::from_secs(delay * 3) {
                    tracing::info!(
                        "System wake detected in server refresh loop (sleep took {:.0}s instead of {}s), waiting 3s for network",
                        elapsed.as_secs_f64(),
                        delay
                    );
                    tokio::time::sleep(std::time::Duration::from_secs(3)).await;
                }
            }
        });
    });

    // Periodic game running check (every 5s) with sleep/wake detection
    use_effect(move || {
        spawn(async move {
            loop {
                let before = std::time::Instant::now();
                tokio::time::sleep(std::time::Duration::from_secs(5)).await;
                let elapsed = before.elapsed();

                if elapsed > std::time::Duration::from_secs(15) {
                    tracing::info!(
                        "System wake detected in game check loop (sleep took {:.0}s instead of 5s)",
                        elapsed.as_secs_f64()
                    );
                }

                check_game_running();
            }
        });
    });

    // System tray event polling (~1s) with sleep/wake detection
    use_effect(move || {
        spawn(async move {
            loop {
                let before = std::time::Instant::now();
                tokio::time::sleep(std::time::Duration::from_secs(1)).await;
                let elapsed = before.elapsed();

                if elapsed > std::time::Duration::from_secs(4) {
                    tracing::info!(
                        "System wake detected in tray poll loop (sleep took {:.1}s instead of 1s)",
                        elapsed.as_secs_f64()
                    );
                }

                crate::platform::tray::poll_tray_events();

                if crate::platform::tray::SHOW_WINDOW_REQUESTED
                    .compare_exchange(true, false, Ordering::AcqRel, Ordering::Acquire)
                    .is_ok()
                {
                    crate::platform::tray::show_main_window();
                }

                if crate::platform::tray::RESTART_REQUESTED
                    .compare_exchange(true, false, Ordering::AcqRel, Ordering::Acquire)
                    .is_ok()
                {
                    crate::platform::restart::restart_app();
                }

                if crate::platform::tray::QUIT_REQUESTED.load(Ordering::Acquire) {
                    crate::config::flush_pending_saves();
                    crate::backend::heartbeat::stop_heartbeat_sync(Some("app_exit".into()));
                    crate::backend::seeding::cleanup_efficiency_on_exit().await;
                    crate::platform::tray::close_app();
                    break;
                }
            }
        });
    });

    let page = *CURRENT_PAGE.read();
    let onboarding_complete = *state::auth::ONBOARDING_COMPLETE.read();

    rsx! {
        // Load Tailwind + DaisyUI CSS via asset!() so it's bundled into the binary
        link { rel: "stylesheet", href: asset!("/assets/tailwind.css") }

        main { class: "h-screen pt-8 pb-16 bg-base-200 flex flex-col",
            // Custom titlebar
            components::titlebar::Titlebar {}

            // Onboarding wizard overlay (shown on first run)
            if !onboarding_complete {
                components::onboarding::Onboarding {}
            }

            // Seed page — always mounted to preserve state
            div { class: if page != 0 { "hidden" } else { "flex-1 min-h-0 overflow-y-auto" },
                components::seed::Seed {}
            }

            // Launch page
            if page == 1 {
                components::launch::Launch {}
            }

            // Leaderboard page
            if page == 2 {
                components::leaderboard::Leaderboard {}
            }

            // Settings page
            if page == 3 {
                components::settings::Settings {}
            }

            // Tools page
            if page == 4 {
                components::tools::Tools {}
            }

            // Bottom navigation
            BottomNav {}

            // Modal overlay
            components::modal::Modal {}

            // Toast notifications
            components::toast::ToastContainer {}
        }
    }
}

#[component]
fn BottomNav() -> Element {
    let page = *CURRENT_PAGE.read();

    rsx! {
        div { class: "dock dock-sm", role: "tablist",
            button {
                class: if page == 0 { "dock-active" } else { "" },
                aria_label: "Seed",
                role: "tab",
                aria_selected: page == 0,
                onclick: move |_| *CURRENT_PAGE.write() = 0,
                img { src: asset!("/assets/logo_nbg.png"), style: "width: 1.25rem; height: 1.25rem; object-fit: contain;", alt: "" }
                span { class: "dock-label text-xs whitespace-nowrap", "Seed" }
            }
            button {
                class: if page == 1 { "dock-active" } else { "" },
                aria_label: "Launch",
                role: "tab",
                aria_selected: page == 1,
                onclick: move |_| *CURRENT_PAGE.write() = 1,
                svg {
                    xmlns: "http://www.w3.org/2000/svg",
                    class: "h-5 w-5",
                    fill: "currentColor",
                    view_box: "0 0 24 24",
                    path { d: "M8 5v14l11-7z" }
                }
                span { class: "dock-label text-xs whitespace-nowrap", "Launch" }
            }
            button {
                class: if page == 2 { "dock-active" } else { "" },
                aria_label: "Leaderboard",
                role: "tab",
                aria_selected: page == 2,
                onclick: move |_| *CURRENT_PAGE.write() = 2,
                svg {
                    xmlns: "http://www.w3.org/2000/svg",
                    class: "h-5 w-5",
                    fill: "none",
                    view_box: "0 0 24 24",
                    stroke: "currentColor",
                    path {
                        stroke_linecap: "round",
                        stroke_linejoin: "round",
                        stroke_width: "2",
                        d: "M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z"
                    }
                }
                span { class: "dock-label text-xs whitespace-nowrap", "Ranks" }
            }
            button {
                class: if page == 3 { "dock-active" } else { "" },
                aria_label: "Settings",
                role: "tab",
                aria_selected: page == 3,
                onclick: move |_| *CURRENT_PAGE.write() = 3,
                svg {
                    xmlns: "http://www.w3.org/2000/svg",
                    class: "h-5 w-5",
                    fill: "none",
                    view_box: "0 0 24 24",
                    stroke: "currentColor",
                    path {
                        stroke_linecap: "round",
                        stroke_linejoin: "round",
                        stroke_width: "2",
                        d: "M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"
                    }
                    path {
                        stroke_linecap: "round",
                        stroke_linejoin: "round",
                        stroke_width: "2",
                        d: "M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                    }
                }
                span { class: "dock-label text-xs whitespace-nowrap", "Settings" }
            }
            button {
                class: if page == 4 { "dock-active" } else { "" },
                aria_label: "Tools",
                role: "tab",
                aria_selected: page == 4,
                onclick: move |_| *CURRENT_PAGE.write() = 4,
                svg {
                    xmlns: "http://www.w3.org/2000/svg",
                    class: "h-5 w-5",
                    fill: "none",
                    view_box: "0 0 24 24",
                    stroke: "currentColor",
                    path {
                        stroke_linecap: "round",
                        stroke_linejoin: "round",
                        stroke_width: "2",
                        d: "M14.7 6.3a1 1 0 000 1.4l1.6 1.6a1 1 0 001.4 0l3.77-3.77a6 6 0 01-7.94 7.94l-6.91 6.91a2.12 2.12 0 01-3-3l6.91-6.91a6 6 0 017.94-7.94l-3.76 3.76z"
                    }
                }
                span { class: "dock-label text-xs whitespace-nowrap", "Tools" }
            }
        }
    }
}

/// App initialization logic — runs once on mount
async fn initialize_app() {
    // Load theme from session
    if let Some(theme) = crate::backend::session::get_stored_session("theme") {
        let _ = document::eval(&format!(
            "document.documentElement.setAttribute('data-theme', '{}')",
            theme
        ));
    }

    // Initialize session state
    state::session::initialize_session().await;

    // Load onboarding / guest mode from config
    let onboarding_complete = crate::backend::session::get_stored_session("onboarding_complete")
        .map(|v| v == "true")
        .unwrap_or(false);
    *state::auth::ONBOARDING_COMPLETE.write() = onboarding_complete;

    let guest_mode = crate::backend::session::get_stored_session("guest_mode")
        .map(|v| v == "true")
        .unwrap_or(false);
    *state::auth::IS_GUEST.write() = guest_mode;

    // Load linked steam IDs from config
    if let Some(ids_json) = crate::backend::session::get_stored_session("linked_steam_ids") {
        if let Ok(ids) = serde_json::from_str::<Vec<String>>(&ids_json) {
            *state::session::LINKED_STEAM_IDS.write() = ids;
        }
    }

    // Initialize auth (restore API key + JWT tokens)
    init_auth().await;

    // Load EU preference
    let eu_enabled = crate::backend::session::get_stored_session("secondary_servers_enabled")
        .map(|v| v == "true")
        .unwrap_or(false);
    *state::servers::EU_ENABLED.write() = eu_enabled;

    // Fetch servers + stats in parallel
    let _ = tokio::join!(fetch_servers(), refresh_server_stats());

    // Start SSE connection for live stats updates
    spawn(crate::api::sse::run_sse_loop());

    // Show toast if startup recovery restored efficiency mode settings
    if let Some(msg) = crate::backend::backup_restore_hll_config::take_startup_restore_notice() {
        let is_error = msg.contains("no backup");
        state::toast::add_toast(
            msg,
            if is_error { state::toast::ToastType::Error } else { state::toast::ToastType::Info },
            Some(if is_error { 15000 } else { 8000 }),
        );
    }

    // Setup event listeners
    state::events::setup_event_listeners();

    // Start stale timer
    state::stale_timer::start_stale_timer();

    // Spawn deferred autoseed if --autoseed-na or --autoseed-eu was passed on CLI
    let cli_autoseed = crate::backend::setup::spawn_pending_autoseed();

    // If no CLI autoseed was triggered, immediately check for missed autoseeds
    // (covers boot-after-scheduled-time without waiting for the 60s polling cycle)
    if !cli_autoseed {
        let missed = crate::backend::autoseed::check_missed_autoseed(false).await;
        if !missed.is_empty() {
            tracing::info!("Startup: triggered missed autoseed for: {:?}", missed);
        }
    }
}

/// Apply a server list response to state signals and backend stores.
/// Shared by initial fetch and background refresh loop.
pub fn apply_server_list(result: crate::api::types::ServersResponse) {
    // Build stats for HLL, preserving existing population data across refreshes.
    // Use HashMap for O(1) lookup by name (handles server reordering between API responses).
    let old_na = state::servers::SERVERS_STATS.read();
    let old_eu = state::servers::EU_SERVERS_STATS.read();

    let old_na_map: std::collections::HashMap<&str, &crate::state::servers::ServerStats> =
        old_na.iter().map(|s| (s.name.as_str(), s)).collect();
    let old_eu_map: std::collections::HashMap<&str, &crate::state::servers::ServerStats> =
        old_eu.iter().map(|s| (s.name.as_str(), s)).collect();

    let na_stats: Vec<crate::state::servers::ServerStats> = result
        .hll
        .na
        .iter()
        .map(|s| {
            let prev = old_na_map.get(s.name.as_str()).copied();
            crate::state::servers::ServerStats {
                name: s.name.clone(),
                map: prev.map(|p| p.map.clone()).unwrap_or_default(),
                players: prev.map(|p| p.players.clone()).unwrap_or_default(),
                max_player_count: prev.and_then(|p| p.max_player_count),
                offline: prev.map(|p| p.offline).unwrap_or(false),
            }
        })
        .collect();
    let eu_stats: Vec<crate::state::servers::ServerStats> = result
        .hll
        .eu
        .iter()
        .map(|s| {
            let prev = old_eu_map.get(s.name.as_str()).copied();
            crate::state::servers::ServerStats {
                name: s.name.clone(),
                map: prev.map(|p| p.map.clone()).unwrap_or_default(),
                players: prev.map(|p| p.players.clone()).unwrap_or_default(),
                max_player_count: prev.and_then(|p| p.max_player_count),
                offline: prev.map(|p| p.offline).unwrap_or(false),
            }
        })
        .collect();

    drop(old_na);
    drop(old_eu);

    // Update legacy flat server lists (backwards compat)
    {
        let mut servers = write_lock!(crate::backend::server::SERVERS);
        *servers = result
            .hll
            .na
            .iter()
            .cloned()
            .map(std::sync::Arc::new)
            .collect();
    }
    {
        let mut servers = write_lock!(crate::backend::server::EU_SERVERS);
        *servers = result
            .hll
            .eu
            .iter()
            .cloned()
            .map(std::sync::Arc::new)
            .collect();
    }

    // Update game-aware server storage
    {
        let mut game_servers = write_lock!(crate::backend::server::GAME_SERVERS);
        game_servers.clear();

        let mut hll_regions = std::collections::HashMap::new();
        hll_regions.insert(
            "na".to_string(),
            result.hll.na.into_iter().map(std::sync::Arc::new).collect(),
        );
        hll_regions.insert(
            "eu".to_string(),
            result.hll.eu.into_iter().map(std::sync::Arc::new).collect(),
        );
        game_servers.insert("hll".to_string(), hll_regions);

        if let Some(hllv) = result.hllv {
            let mut hllv_regions = std::collections::HashMap::new();
            hllv_regions.insert(
                "na".to_string(),
                hllv.na.into_iter().map(std::sync::Arc::new).collect(),
            );
            hllv_regions.insert(
                "eu".to_string(),
                hllv.eu.into_iter().map(std::sync::Arc::new).collect(),
            );
            game_servers.insert("hllv".to_string(), hllv_regions);
        }
    }

    // Update game-aware stats
    {
        let mut game_stats = std::collections::HashMap::new();
        let mut hll_stats = std::collections::HashMap::new();
        hll_stats.insert("na".to_string(), na_stats.clone());
        hll_stats.insert("eu".to_string(), eu_stats.clone());
        game_stats.insert("hll".to_string(), hll_stats);
        *crate::state::servers::GAME_SERVER_STATS.write() = game_stats;
    }

    *state::servers::SERVERS_STATS.write() = na_stats;
    *state::servers::EU_SERVERS_STATS.write() = eu_stats;
    *state::servers::SERVER_LOAD_ERROR.write() = false;
}

/// Fetch server list from API and populate state
pub async fn fetch_servers() {
    match crate::api::client::get_servers().await {
        Ok(result) => {
            apply_server_list(result);
        }
        Err(e) => {
            tracing::error!("Error getting servers: {}", e);
            let msg = crate::api::client::friendly_error(e.as_ref());
            state::toast::add_toast(
                &format!("Failed to load servers: {}", msg),
                state::toast::ToastType::Error,
                None,
            );
            *state::servers::SERVER_LOAD_ERROR.write() = true;
        }
    }
}

/// Build a ServerStats from an incoming batch result and the current entry.
/// Returns None if the result is an error (should be skipped).
fn build_updated_stat(
    r: &crate::api::types::BatchStatsResult,
    current: &crate::state::servers::ServerStats,
) -> Option<crate::state::servers::ServerStats> {
    if r.offline {
        Some(crate::state::servers::ServerStats {
            name: current.name.clone(),
            map: "Offline".to_string(),
            players: "0".to_string(),
            max_player_count: current.max_player_count,
            offline: true,
        })
    } else if r.error.is_some() {
        None
    } else {
        Some(crate::state::servers::ServerStats {
            name: current.name.clone(),
            map: r.map_name.clone().unwrap_or_default(),
            players: r.player_count.map(|p| p.to_string()).unwrap_or_else(|| "0".to_string()),
            max_player_count: r.max_player_count.or(current.max_player_count),
            offline: false,
        })
    }
}

/// Apply a stats update to the UI signals. Shared by SSE and polling paths.
///
/// Avoids cloning entire signal collections upfront — instead collects only changed
/// entries, then applies them with targeted signal writes.
pub fn apply_stats_update(all_stats: Vec<crate::api::types::BatchStatsResult>) {
    // Collect changes by diffing against current state (read-only pass)
    struct StatChange {
        game: String,
        region: String,
        index: usize,
        stat: crate::state::servers::ServerStats,
    }

    let changes: Vec<StatChange> = {
        let game_stats = state::servers::GAME_SERVER_STATS.read();

        all_stats
            .iter()
            .filter_map(|r| {
                let game_key = if r.game.is_empty() { "hll" } else { r.game.as_str() };
                let regions = game_stats.get(game_key)?;
                let stats = regions.get(&r.region)?;
                if r.index >= stats.len() {
                    return None;
                }
                let current = &stats[r.index];
                let new_stat = build_updated_stat(r, current)?;

                if current.players != new_stat.players || current.map != new_stat.map {
                    Some(StatChange {
                        game: game_key.to_string(),
                        region: r.region.clone(),
                        index: r.index,
                        stat: new_stat,
                    })
                } else {
                    None
                }
            })
            .collect()
    }; // read guard dropped

    // Apply game-aware changes
    if !changes.is_empty() {
        let mut game_stats = state::servers::GAME_SERVER_STATS.write();
        for change in &changes {
            if let Some(regions) = game_stats.get_mut(&change.game) {
                if let Some(stats) = regions.get_mut(&change.region) {
                    if change.index < stats.len() {
                        if change.stat.offline {
                            crate::backend::server::mark_server_offline(&change.stat.name);
                        } else {
                            crate::backend::server::clear_server_offline(&change.stat.name);
                            if let (Ok(players), Some(max)) = (
                                change.stat.players.parse::<i32>(),
                                change.stat.max_player_count,
                            ) {
                                crate::backend::server::update_player_count(
                                    &change.stat.name, players, max,
                                );
                            }
                        }
                        stats[change.index] = change.stat.clone();
                    }
                }
            }
        }
    }

    // Apply legacy flat signal changes (HLL only)
    let has_na = changes.iter().any(|c| c.game == "hll" && c.region != "eu");
    let has_eu = changes.iter().any(|c| c.game == "hll" && c.region == "eu");

    if has_na {
        let mut w = state::servers::SERVERS_STATS.write();
        for change in changes.iter().filter(|c| c.game == "hll" && c.region != "eu") {
            if change.index < w.len() {
                w[change.index] = change.stat.clone();
            }
        }
    }
    if has_eu {
        let mut w = state::servers::EU_SERVERS_STATS.write();
        for change in changes.iter().filter(|c| c.game == "hll" && c.region == "eu") {
            if change.index < w.len() {
                w[change.index] = change.stat.clone();
            }
        }
    }

    // Always update the staleness timer on successful fetch,
    // even when data hasn't changed.
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64;
    *state::servers::LAST_STATS_UPDATE.write() = now;
}

/// Refresh server stats from API
pub async fn refresh_server_stats() {
    match crate::api::client::get_stats().await {
        Ok(all_stats) => {
            apply_stats_update(all_stats);

        }
        Err(e) => {
            tracing::error!("Error refreshing server stats: {}", e);
        }
    }
}

/// Convert between the two AuthProvider enums (api::types vs state::auth)
pub fn convert_auth_provider(p: crate::api::types::AuthProvider) -> state::auth::AuthProvider {
    match p {
        crate::api::types::AuthProvider::Steam => state::auth::AuthProvider::Steam,
        crate::api::types::AuthProvider::Discord => state::auth::AuthProvider::Discord,
        crate::api::types::AuthProvider::Guest => state::auth::AuthProvider::Guest,
    }
}

/// Convert API UserInfo to state UserInfo
pub fn convert_user_info(me: crate::api::types::UserInfo) -> state::auth::UserInfo {
    state::auth::UserInfo {
        user_id: me.user_id,
        username: me.username,
        auth_provider: convert_auth_provider(me.auth_provider),
        display_name: me.display_name,
        steam_id: me.steam_id,
        discord_id: me.discord_id,
        avatar: me.avatar,
        created_at: me.created_at,
        last_seen_at: me.last_seen_at,
        leaderboard_opt_out: me.leaderboard_opt_out,
    }
}

/// Check if a JWT token is expired by decoding the payload and reading the `exp` claim.
/// Returns `true` if expired or if decoding fails (fail-safe).
fn is_jwt_expired(token: &str) -> bool {
    use base64::engine::{general_purpose::URL_SAFE_NO_PAD, Engine};

    let parts: Vec<&str> = token.split('.').collect();
    if parts.len() != 3 {
        return true;
    }
    let payload = match URL_SAFE_NO_PAD.decode(parts[1]) {
        Ok(bytes) => bytes,
        Err(_) => return true,
    };
    let json: serde_json::Value = match serde_json::from_slice(&payload) {
        Ok(v) => v,
        Err(_) => return true,
    };
    let exp = match json.get("exp").and_then(|v| v.as_i64()) {
        Some(e) => e,
        None => return true,
    };
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64;
    exp <= now
}

/// Initialize auth from stored API key and/or JWT tokens
async fn init_auth() {
    *state::auth::AUTH_LOADING.write() = true;

    // Check for stored API key first
    let api_key = crate::backend::session::get_stored_session("api_key");
    if let Some(ref key) = api_key {
        if !key.is_empty() {
            crate::api::client::set_api_key(key);
            *state::auth::AUTH_METHOD.write() = state::auth::AuthMethod::ApiKey;
            tracing::info!("API key restored from config");
        }
    }

    // Check for JWT tokens
    let token = crate::backend::session::get_stored_session("auth_token");
    let refresh = crate::backend::session::get_stored_session("auth_refresh_token");

    if let (Some(token), Some(refresh)) = (token, refresh) {
        if !token.is_empty() && !refresh.is_empty() {
            // If both tokens are expired, skip the network round-trip
            if is_jwt_expired(&token) && is_jwt_expired(&refresh) {
                tracing::info!("Stored JWT tokens expired, clearing");
                let _ = crate::backend::session::store_session("auth_token", String::new());
                let _ = crate::backend::session::store_session("auth_refresh_token", String::new());
            } else {
                crate::api::client::set_tokens(&token, &refresh);

                match crate::api::client::get_me().await {
                    Ok(me) => {
                        *state::auth::USER.write() = Some(convert_user_info(me));
                        *state::auth::IS_LOGGED_IN.write() = true;
                        *state::auth::AUTH_METHOD.write() = state::auth::AuthMethod::Jwt;
                        tracing::info!("Auth restored from stored JWT tokens");

                        // Refresh linked data in parallel after successful JWT restore
                        let _ = tokio::join!(
                            state::session::refresh_steam_ids_from_api(),
                            state::session::refresh_linked_providers(),
                        );

                        *state::auth::AUTH_LOADING.write() = false;
                        return;
                    }
                    Err(_) => {
                        tracing::info!("Stored JWT tokens invalid, falling back");
                    }
                }
            }
        }
    }

    // Fallback: try get_me with API key if JWT failed
    if api_key.as_ref().map(|k| !k.is_empty()).unwrap_or(false) {
        match crate::api::client::get_me().await {
            Ok(me) => {
                // Sync IS_GUEST from server's auth_provider (handles upgrade case
                // where guest linked OAuth between sessions)
                let is_guest = me.auth_provider == crate::api::types::AuthProvider::Guest;
                *state::auth::IS_GUEST.write() = is_guest;
                if is_guest {
                    *state::auth::AUTH_PROVIDER.write() = Some(state::auth::AuthProvider::Guest);
                } else {
                    let provider = convert_auth_provider(me.auth_provider.clone());
                    *state::auth::AUTH_PROVIDER.write() = Some(provider);
                    // Guest was upgraded — persist the change locally
                    let _ = crate::backend::session::store_session("guest_mode", "false".to_string());
                    let _ = crate::backend::session::store_session("auth_provider", me.auth_provider.to_string());
                }

                *state::auth::USER.write() = Some(convert_user_info(me));
                *state::auth::IS_LOGGED_IN.write() = true;
                tracing::info!("Auth restored via API key");

                // Refresh linked data in parallel after successful API key auth
                let _ = tokio::join!(
                    state::session::refresh_steam_ids_from_api(),
                    state::session::refresh_linked_providers(),
                );
            }
            Err(e) => {
                tracing::warn!("Both JWT and API key auth failed (server DB may have been reset): {}", e);

                // Full credential reset — clears stale API key so guest-ok
                // endpoints (get_servers, get_stats) aren't poisoned with 401s
                crate::api::client::clear_tokens();
                crate::api::client::clear_api_key();
                *state::auth::USER.write() = None;
                *state::auth::IS_LOGGED_IN.write() = false;
                *state::auth::AUTH_METHOD.write() = state::auth::AuthMethod::None;

                // Clear persistent config so stale creds don't come back
                let _ = crate::backend::session::store_session("auth_token", String::new());
                let _ = crate::backend::session::store_session("auth_refresh_token", String::new());
                let _ = crate::backend::session::store_session("api_key", String::new());
                let _ = crate::backend::session::store_session("onboarding_complete", String::new());
                let _ = crate::backend::session::store_session("guest_mode", String::new());

                // Reset onboarding so the registration wizard appears
                *state::auth::ONBOARDING_COMPLETE.write() = false;
                *state::auth::ONBOARDING_STEP.write() = 0;
                *state::auth::IS_GUEST.write() = false;
            }
        }
    }

    *state::auth::AUTH_LOADING.write() = false;
}

/// Check if the current game is running and update seeding status
fn check_game_running() {
    use crate::state::seeding::*;

    let game = crate::backend::seeding::current_game();
    let running = crate::backend::process::is_game_running(game);
    let status = SEEDING_STATUS.read().clone();

    if matches!(status, SeedingStatus::Initializing | SeedingStatus::Stopping) {
        return;
    }

    if running {
        if !matches!(status, SeedingStatus::Seeding) {
            *SEEDING_STATUS.write() = SeedingStatus::Running;
        }
    } else if !matches!(status, SeedingStatus::Idle) {
        *SEEDING_STATUS.write() = SeedingStatus::Stopped;
    }
}
