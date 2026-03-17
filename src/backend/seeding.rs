use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::RwLock as StdRwLock;
use std::time::{self, Duration};

use log::{info, debug, warn};
use once_cell::sync::Lazy;
use rand::prelude::*;
use tokio::time::sleep;

use crate::error::AppError;
use crate::events::{send_event, AppEvent};

use crate::backend::backup_restore_hll_config::{
    backup_config, invalidate_config_cache, is_config_overwritten,
    restore_after_seeding, restore_config,
};
use crate::backend::game::{GameDefinition, HLL};
use crate::config::get;
use crate::backend::process::{
    check_launch_processes, is_game_loading, is_process_running, kill_processes_by_name,
};
use crate::backend::server::{
    get_server_by_region, ServerInfo,
};
use crate::backend::steam::open_hll;
use crate::backend::window_focus;
use crate::backend::window_focus::focus_esprit_seeder;

// ============================================================================
// GLOBAL STATE
// ============================================================================

/// The game currently being seeded. Defaults to HLL when idle.
static CURRENT_GAME: Lazy<StdRwLock<&'static GameDefinition>> = Lazy::new(|| StdRwLock::new(&HLL));

// Global stop flag - set to true when user requests stop, checked by seeding loops
static STOP_REQUESTED: AtomicBool = AtomicBool::new(false);

// Track if we've already backed up config this session to avoid redundant backups
static CONFIG_BACKED_UP: AtomicBool = AtomicBool::new(false);

// Track if we've already warmed the API cache (only do it once on startup)
pub static CACHE_WARMED: AtomicBool = AtomicBool::new(false);

// Track whether a launch watcher task is actively monitoring for phantom HLL launches
static LAUNCH_WATCHER_ACTIVE: AtomicBool = AtomicBool::new(false);

// Global flag for cancelling autoseed countdown
pub static AUTOSEED_CANCELLED: AtomicBool = AtomicBool::new(false);
// Guard against concurrent autoseed operations
pub static AUTOSEED_IN_PROGRESS: AtomicBool = AtomicBool::new(false);

// Server-switch snooze coordination (0 = not snoozed, >0 = snoozed for N seconds)
static SNOOZE_DURATION_SECS: AtomicU64 = AtomicU64::new(0);
static SWITCH_NOW_REQUESTED: AtomicBool = AtomicBool::new(false);

// ============================================================================
// TIMING CONSTANTS
// ============================================================================

// Splash bypass duration limits (seconds)
pub const SPLASH_BYPASS_MIN_SECS: u64 = 10;
pub const SPLASH_BYPASS_MAX_SECS: u64 = 60;
const SPLASH_BYPASS_DEFAULT_SECS: u64 = 20;

// Game launch timeouts
const EAC_LAUNCH_TIMEOUT_SECS: u64 = 180;
const WINDOW_WAIT_TIMEOUT_SECS: u64 = 60;
const GAME_OPEN_FIRST_RETRY_SECS: u64 = 60;
const GAME_OPEN_SECOND_RETRY_SECS: u64 = 120;
const GAME_OPEN_TIMEOUT_SECS: u64 = 180;

// Seeding timeouts
const MAX_SEEDING_DURATION_SECS: u64 = 60 * 60 * 5; // 5 hours
const POST_KILL_WAIT_SECS: u64 = 20;
const SERVER_SWITCH_COUNTDOWN_SECS: u64 = 30;

// Launch watcher: kills phantom HLL launches after a failed seeding start
const LAUNCH_WATCHER_TIMEOUT_SECS: u64 = 600; // 10 min

// Monitor loop polling intervals (linear backoff)
const MONITOR_MIN_INTERVAL_SECS: u64 = 15;
const MONITOR_MAX_INTERVAL_SECS: u64 = 60;
const MONITOR_BACKOFF_STEP_SECS: u64 = 15;

// Server switch stagger: max delay based on server fill level.
// Full server → ~0s, barely at threshold → STAGGER_MAX_SECS.
const STAGGER_MAX_SECS: u64 = 90;
// Per-client random jitter on top of fill-based stagger
const STAGGER_JITTER_MAX_SECS: u64 = 15;

// Bypass SSE cache and do a direct HTTP fetch this often (catches missed SSE events)
const FORCED_REFRESH_INTERVAL_SECS: u64 = 180;

// Log monitor status at INFO level this often
const STATUS_LOG_INTERVAL_SECS: u64 = 300;

// ============================================================================
// STOP/SNOOZE HELPERS
// ============================================================================

pub fn is_stop_requested() -> bool {
    STOP_REQUESTED.load(Ordering::Acquire)
}

pub fn request_stop() {
    STOP_REQUESTED.store(true, Ordering::Release);
}

pub fn clear_stop() {
    STOP_REQUESTED.store(false, Ordering::Release);
    // Reset backup flag so a new session will perform one backup
    reset_config_backup_flag();
}

pub fn current_game() -> &'static GameDefinition {
    *read_lock!(CURRENT_GAME)
}

pub fn set_current_game(game: &'static GameDefinition) {
    *write_lock!(CURRENT_GAME) = game;
}

fn cancel_launch_watcher() {
    LAUNCH_WATCHER_ACTIVE.store(false, Ordering::Release);
}

fn spawn_launch_watcher(server_info: Arc<ServerInfo>, server_index: usize, region: String) {
    // Only spawn if no watcher is already running
    if LAUNCH_WATCHER_ACTIVE.compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire).is_err() {
        return;
    }
    tokio::spawn(async move {
        // Brief delay so the frontend processes the invoke error before we emit waiting state
        sleep(Duration::from_millis(500)).await;

        if !LAUNCH_WATCHER_ACTIVE.load(Ordering::Acquire) {
            info!("Launch watcher cancelled before starting");
            return;
        }

        send_event(AppEvent::SeedingUpdateWaiting {
            server_index,
            region: region.clone(),
        });

        let start = std::time::Instant::now();
        loop {
            sleep(Duration::from_secs(10)).await;
            if !LAUNCH_WATCHER_ACTIVE.load(Ordering::Acquire) {
                info!("Launch watcher cancelled");
                return;
            }
            if start.elapsed().as_secs() >= LAUNCH_WATCHER_TIMEOUT_SECS {
                info!("Launch watcher timed out after {} seconds", LAUNCH_WATCHER_TIMEOUT_SECS);
                LAUNCH_WATCHER_ACTIVE.store(false, Ordering::Release);
                send_event(AppEvent::SeedingUpdateTimeout);
                return;
            }
            if is_process_running("HLL-Win64-Shipping.exe") {
                warn!("Launch watcher detected phantom HLL launch after failed seeding start, killing");
                kill_hll_process();

                // Wait for process to fully exit
                sleep(Duration::from_secs(POST_KILL_WAIT_SECS)).await;

                if !LAUNCH_WATCHER_ACTIVE.load(Ordering::Acquire) {
                    info!("Launch watcher cancelled after killing phantom");
                    return;
                }
                if is_stop_requested() {
                    info!("Stop requested, launch watcher exiting");
                    LAUNCH_WATCHER_ACTIVE.store(false, Ordering::Release);
                    return;
                }

                // Retry seeding now that the update should be done
                info!("Launch watcher retrying seeding for {}", server_info.short_name);
                match start_seeding_inner(&server_info).await {
                    Ok(()) => {
                        if is_stop_requested() {
                            info!("Stop requested during restart, exiting watcher");
                            guard_config_after_close().await;
                            restore_after_seeding();
                            LAUNCH_WATCHER_ACTIVE.store(false, Ordering::Release);
                            return;
                        }

                        info!("Launch watcher successfully restarted seeding for {}", server_info.short_name);
                        LAUNCH_WATCHER_ACTIVE.store(false, Ordering::Release);

                        let region_for_monitor = region.clone();
                        send_event(AppEvent::SeedingUpdateStarted {
                            server_index,
                            region,
                        });

                        // Run monitor loop directly — frontend will NOT call monitorSeed
                        let elapsed = time::Instant::now();
                        let _ = monitor_loop(elapsed, &server_info, &region_for_monitor, server_index).await;
                        restore_after_seeding();
                        return;
                    }
                    Err(e) => {
                        info!("Launch watcher restart failed: {:?}, continuing to watch", e);
                        restore_after_seeding();
                        // Keep watching — Steam may have more buffered commands
                    }
                }
            }
        }
    });
}

fn is_switch_snoozed() -> bool { SNOOZE_DURATION_SECS.load(Ordering::Acquire) > 0 }
fn set_switch_snooze(duration_secs: u64) {
    SNOOZE_DURATION_SECS.store(duration_secs.max(1), Ordering::Release);
}
fn take_snooze_duration() -> u64 {
    SNOOZE_DURATION_SECS.swap(0, Ordering::AcqRel)
}
fn clear_switch_state() {
    SNOOZE_DURATION_SECS.store(0, Ordering::Release);
    SWITCH_NOW_REQUESTED.store(false, Ordering::Release);
}
fn is_switch_now_requested() -> bool { SWITCH_NOW_REQUESTED.load(Ordering::Acquire) }
fn request_switch_now() { SWITCH_NOW_REQUESTED.store(true, Ordering::Release); }

// ============================================================================
// CONFIG BACKUP HELPERS
// ============================================================================

pub fn check_and_restore_config() {
    if is_config_overwritten() {
        info!("Config is reverted to default, restoring");
        restore_config();
    } else if !CONFIG_BACKED_UP.load(Ordering::Acquire) {
        // Only backup once per session to avoid redundant disk I/O
        info!("Config is not overwritten. Performing a backup");
        backup_config();
        CONFIG_BACKED_UP.store(true, Ordering::Release);
    }
}

/// After the game process exits, wait briefly for any config writes to finish,
/// then check if the game reset the config to defaults and restore from backup.
/// HLL's crash recovery overwrites GameUserSettings.ini on ungraceful exit.
async fn guard_config_after_close() {
    if !CONFIG_BACKED_UP.load(Ordering::Acquire) {
        return; // No backup to restore from
    }
    // Give the game time to finish writing its reset config
    sleep(Duration::from_secs(2)).await;
    // Force a fresh file read — cached mtime is stale
    invalidate_config_cache();
    if is_config_overwritten() {
        info!("Game reset config on exit, restoring from backup");
        restore_config();
    }
}

pub fn reset_config_backup_flag() {
    CONFIG_BACKED_UP.store(false, Ordering::Release);
}

// ============================================================================
// KILL HELPERS
// ============================================================================

pub fn kill_hll_process() {
    check_and_restore_config();
    kill_processes_by_name("HLL-Win64-Shipping.exe");
    window_focus::invalidate_hwnd_cache();
}

pub fn kill_game_process(game: &GameDefinition) {
    check_and_restore_config();
    crate::backend::process::kill_game_processes(game);
    crate::backend::window_focus::invalidate_hwnd_cache();
}

/// Check if the seeding candidate for this region has changed.
/// Reads from the SSE-derived cache when fresh, falling back to polling the API.
/// When `force_http` is true, bypasses the cache entirely for a fresh HTTP fetch.
/// Returns `true` when the monitor loop should trigger a server switch.
/// API errors return `false` to avoid exiting on transient failures.
async fn has_candidate_changed(game: &str, region: &str, index: usize, force_http: bool) -> bool {
    let status = if force_http {
        // Bypass cache — get authoritative status from the API
        match crate::backend::api_client::fetch_seeding_status().await {
            Ok(s) => {
                crate::backend::api_client::update_seeding_status_cache(s.clone());
                s
            }
            Err(e) => {
                warn!("Forced refresh failed: {}, falling back to cache", e);
                match crate::backend::api_client::get_cached_seeding_status() {
                    Some(s) => s,
                    None => {
                        warn!("No cached seeding status available either");
                        return false;
                    }
                }
            }
        }
    } else {
        // Prefer SSE-derived cache (no network round-trip); fall back to polling
        match crate::backend::api_client::get_cached_seeding_status() {
            Some(s) => s,
            None => match crate::backend::api_client::fetch_seeding_status().await {
                Ok(s) => {
                    crate::backend::api_client::update_seeding_status_cache(s.clone());
                    s
                }
                Err(e) => {
                    warn!("Failed to fetch seeding status: {}, continuing monitoring", e);
                    return false;
                }
            },
        }
    };

    let game_status = match game {
        "hll" => Some(&status.hll),
        "hllv" => status.hllv.as_ref(),
        _ => None,
    };
    let candidate = game_status.and_then(|gs| {
        if region == "eu" { gs.eu.as_ref() } else { gs.na.as_ref() }
    });
    match candidate {
        None => {
            info!("No candidate for {}:{}, triggering switch", game, region);
            true
        }
        Some(c) if c.index != index => {
            info!("Candidate changed from {} to {} in {}:{}, triggering switch", index, c.index, game, region);
            true
        }
        _ => false,
    }
}

// ============================================================================
// COMMANDS
// ============================================================================

pub fn cancel_autoseed() {
    info!("Autoseed countdown cancelled by user");
    AUTOSEED_CANCELLED.store(true, Ordering::Release);
    AUTOSEED_IN_PROGRESS.store(false, Ordering::Release);
}

pub fn snooze_server_switch(duration_secs: u64) {
    let clamped = duration_secs.clamp(60, 1800);
    info!("Server switch snoozed for {}s by user", clamped);
    set_switch_snooze(clamped);
}

pub fn confirm_server_switch() {
    info!("Server switch confirmed immediately by user");
    request_switch_now();
}

pub async fn start_seeding(server_number: usize) -> Result<usize, AppError> {
    start_seeding_impl(server_number, "na").await
}

pub async fn start_seeding_eu(server_number: usize) -> Result<usize, AppError> {
    start_seeding_impl(server_number, "eu").await
}


pub async fn start(server_number: usize) -> Result<(), AppError> {
    start_impl(server_number, "na").await
}

pub async fn start_eu(server_number: usize) -> Result<(), AppError> {
    start_impl(server_number, "eu").await
}

pub async fn stop_seeding() -> Result<(), AppError> {
    info!("Stopping Seeding");
    request_stop();
    cancel_launch_watcher();

    if is_game_loading() {
        info!("Waiting for HLL to finish loading");
        let mut i = 0;
        while i < 60 {
            if is_process_running("HLL-Win64-Shipping.exe") && !is_game_loading() {
                info!("HLL is done loading, waiting 2 seconds so Easy Anti-Cheat doesnt get stuck");
                sleep(Duration::from_secs(2)).await;
                kill_hll_process();
                guard_config_after_close().await;
                restore_after_seeding();
                return Ok(());
            }

            sleep(Duration::from_secs(1)).await;
            i += 1;
        }
        info!("HLL must have stopped on its own, aborting");
        guard_config_after_close().await;
        restore_after_seeding();
        return Ok(());
    }

    kill_hll_process();

    guard_config_after_close().await;
    restore_after_seeding();

    info!("Seeding stopped. Shutdown Complete.");

    Ok(())
}

pub async fn stop_seeding_only() -> Result<(), AppError> {
    info!("Stopping seeding (keeping game running)");
    request_stop();
    cancel_launch_watcher();
    restore_after_seeding();
    window_focus::invalidate_hwnd_cache();
    info!("Seeding stopped. Game still running.");
    Ok(())
}

/// If efficiency mode settings are applied and the game is running, kill the
/// game and restore the user's original settings before exiting the app.
/// Called from exit paths (tray quit, titlebar close) to prevent leaving the
/// game in a degraded state.
pub async fn cleanup_efficiency_on_exit() {
    use crate::backend::backup_restore_hll_config::is_efficiency_mode_applied;

    if !is_efficiency_mode_applied() {
        return;
    }

    let game = current_game();
    if !crate::backend::process::is_game_running(game) {
        // Game not running — just restore settings
        restore_after_seeding();
        return;
    }

    info!("Efficiency mode active on exit — killing game and restoring settings");
    kill_game_process(game);

    // Wait up to 10s for the process to die
    for _ in 0..20 {
        if !crate::backend::process::is_game_running(game) {
            break;
        }
        sleep(Duration::from_millis(500)).await;
    }

    restore_after_seeding();
}

pub async fn monitor_seed(server_number: usize) -> Result<(), AppError> {
    monitor_seed_impl(server_number, "na").await
}

pub async fn monitor_seed_eu(server_number: usize) -> Result<(), AppError> {
    monitor_seed_impl(server_number, "eu").await
}

// ============================================================================
// INTERNAL FUNCTIONS
// ============================================================================

async fn start_seeding_impl(server_number: usize, region: &str) -> Result<usize, AppError> {
    clear_stop();
    cancel_launch_watcher();
    if is_process_running("HLL-Win64-Shipping.exe") {
        info!("HLL is already running, exiting start_seeding ({})", region);
        return Ok(server_number);
    }

    let server_info = get_server_by_region(region, server_number)?;

    if let Err(e) = start_seeding_inner(&server_info).await {
        restore_after_seeding();
        spawn_launch_watcher(Arc::clone(&server_info), server_number, region.to_string());
        return Err(e);
    }

    info!("Seeding started ({} region). Monitor process will now run. - {}", region.to_uppercase(), server_info.name);
    Ok(server_number)
}

async fn start_impl(server_number: usize, region: &str) -> Result<(), AppError> {
    cancel_launch_watcher();
    let server_info = get_server_by_region(region, server_number)?;

    // Direct launch: do NOT apply efficiency mode
    open_hll(&server_info, false).await.map_err(|e| AppError::new(format!("Failed to open HLL: {}", e)))?;
    focus_hll().await?;

    info!("Start {} complete.", region.to_uppercase());
    Ok(())
}

async fn monitor_seed_impl(server_number: usize, region: &str) -> Result<(), AppError> {
    info!("Monitoring {} seed running", region.to_uppercase());
    let elapsed_time = time::Instant::now();

    let server_info = get_server_by_region(region, server_number)?;

    monitor_loop(elapsed_time, &server_info, region, server_number).await?;
    Ok(())
}

async fn focus_hll() -> Result<(), AppError> {
    // Read configurable bypass duration with validation (clamped to safe range)
    let bypass_duration_secs: u64 = get("splash_bypass_duration")
        .and_then(|v| v.as_str().and_then(|s| s.parse::<u64>().ok()))
        .unwrap_or(SPLASH_BYPASS_DEFAULT_SECS)
        .clamp(SPLASH_BYPASS_MIN_SECS, SPLASH_BYPASS_MAX_SECS);

    info!("Splash bypass: watching for EAC bootstrapper to finish");

    // Phase 1: Wait for EAC bootstrapper (Launch_HLL.exe) to finish.
    // The launch sequence is: Launch_HLL.exe (EAC) -> HLL-Win64-Shipping.exe (game)
    // We wait until Launch_HLL.exe disappears AND the game process is running.
    // Uses adaptive backoff: starts at 1s, increases to 3s max
    let wait_start = time::Instant::now();
    let mut saw_eac = false;
    let mut eac_interval_ms: u64 = 1000;
    loop {
        if is_stop_requested() {
            info!("Stop requested during splash bypass phase 1 (EAC wait)");
            return Ok(());
        }

        let (eac_running, hll_running) = check_launch_processes();

        if eac_running {
            saw_eac = true;
            info!("EAC bootstrapper running, waiting for it to finish...");
            // Reset interval when we see activity
            eac_interval_ms = 1000;
        } else {
            // Backoff when waiting
            eac_interval_ms = (eac_interval_ms + 500).min(3000);
        }

        // EAC finished (or was never seen) and HLL is now running
        if !eac_running && hll_running {
            if saw_eac {
                info!("EAC bootstrapper finished after {}s, HLL process started", wait_start.elapsed().as_secs());
            } else {
                info!("HLL process running (EAC already finished) after {}s", wait_start.elapsed().as_secs());
            }
            break;
        }

        if wait_start.elapsed().as_secs() > EAC_LAUNCH_TIMEOUT_SECS {
            info!("Timeout waiting for game launch after {}s, aborting splash bypass", EAC_LAUNCH_TIMEOUT_SECS);
            send_event(AppEvent::SplashBypassTimeout {
                reason: "Game did not launch within timeout".to_string(),
            });
            return Ok(());
        }
        sleep(Duration::from_millis(eac_interval_ms)).await;
    }

    // Phase 2: Wait for HLL window to appear.
    // The process is running but window may take a moment to create.
    // Uses adaptive backoff: starts at 250ms, increases to 1s max
    info!("Waiting for HLL window to appear...");
    let window_wait_start = time::Instant::now();
    let mut window_interval_ms: u64 = 250;
    loop {
        if is_stop_requested() {
            info!("Stop requested during splash bypass phase 2 (window wait)");
            return Ok(());
        }
        if window_focus::find_hll_hwnd().is_some() {
            info!("HLL window found after {}s", window_wait_start.elapsed().as_secs());
            break;
        }
        if window_wait_start.elapsed().as_secs() > WINDOW_WAIT_TIMEOUT_SECS {
            info!("HLL window not found after {}s, aborting splash bypass", WINDOW_WAIT_TIMEOUT_SECS);
            send_event(AppEvent::SplashBypassTimeout {
                reason: "Game window did not appear".to_string(),
            });
            return Ok(());
        }
        sleep(Duration::from_millis(window_interval_ms)).await;
        // Gradually increase interval
        window_interval_ms = (window_interval_ms + 250).min(1000);
    }

    // Notify frontend so it can show a countdown
    send_event(AppEvent::SplashBypassStarted {
        duration_secs: bypass_duration_secs,
    });

    // Phase 3: Send keys via multiple methods with adaptive intervals.
    //
    // Method 1: PostMessage - sends input directly to HLL's message queue.
    //           Works on Win10 locked, may fail on Win11 locked.
    // Method 2: UI Automation + SendInput - alternative for Win11 locked screens.
    //           Uses Windows accessibility framework which may bypass some restrictions.
    //
    // First 30s: Escape (skip videos) + F13 (dismiss splash) - faster interval
    // After 30s: F13 only with longer intervals since splash is likely dismissed
    // F13 is a valid virtual key that UE4 sees as "any button" input
    // but is never bound to anything in-game.
    let is_win11 = crate::backend::win11_input::is_windows_11_or_later();
    if is_win11 {
        info!("Windows 11 detected - will use additional input methods");
    }

    let bypass_start = time::Instant::now();
    let mut attempt: u32 = 0;
    while bypass_start.elapsed().as_secs() < bypass_duration_secs {
        if is_stop_requested() {
            info!("Stop requested during splash bypass phase 3 (key sending)");
            return Ok(());
        }

        attempt += 1;
        let elapsed = bypass_start.elapsed().as_secs();
        let send_escape = elapsed < 30;

        // Adaptive interval: faster in first 15s, slower after 30s
        let key_interval_secs = if elapsed < 15 { 1 } else if elapsed < 30 { 2 } else { 3 };

        debug!("Splash bypass attempt {} ({}s / {}s) [escape={}, interval={}s]",
              attempt, elapsed, bypass_duration_secs, send_escape, key_interval_secs);

        // Method 1: PostMessage (original approach)
        // Run on a blocking thread to avoid freezing the UI event loop
        // (these functions use std::thread::sleep for Windows API timing).
        match tokio::task::spawn_blocking(move || window_focus::send_keys_to_hll(send_escape)).await {
            Ok(Ok(true)) => debug!("Sent {}F13 to HLL via PostMessage", if send_escape { "Escape+" } else { "" }),
            Ok(Ok(false)) => {
                info!("HLL window not found, game may have been closed");
                break;
            }
            Ok(Err(e)) => info!("send_keys_to_hll error: {}", e),
            Err(e) => info!("send_keys_to_hll task panicked: {}", e),
        }

        // Method 2: UI Automation + SendInput (Win11 fallback)
        // Try this on every attempt since we don't know if screen is locked
        if is_win11 {
            match tokio::task::spawn_blocking(move || crate::backend::win11_input::send_keys_via_uia(send_escape)).await {
                Ok(Ok(true)) => debug!("Sent {}F13 via UI Automation + SendInput", if send_escape { "Escape+" } else { "" }),
                Ok(Ok(false)) => debug!("UI Automation: window not found"),
                Ok(Err(e)) => debug!("UI Automation error: {}", e),
                Err(e) => debug!("UI Automation task panicked: {}", e),
            }
        }

        sleep(Duration::from_secs(key_interval_secs)).await;
    }

    // Phase 4: Final focus attempts as a last resort.
    // Try multiple methods to maximize chance of success.
    // Run on blocking threads to avoid freezing the UI event loop.
    info!("Final focus attempt with AttachThreadInput");
    match tokio::task::spawn_blocking(|| window_focus::force_focus_and_send_key()).await {
        Ok(Ok(true)) => info!("Final focus: sent input to HLL"),
        Ok(Ok(false)) => info!("Final focus: HLL window not found"),
        Ok(Err(e)) => info!("Final focus error: {}", e),
        Err(e) => info!("Final focus task panicked: {}", e),
    }

    // Win11: Also try UI Automation as final attempt
    if is_win11 {
        info!("Final attempt with UI Automation (Win11)");
        match tokio::task::spawn_blocking(|| crate::backend::win11_input::try_all_input_methods(false)).await {
            Ok(Ok(true)) => info!("UI Automation final attempt succeeded"),
            Ok(Ok(false)) => info!("UI Automation final attempt: window not found"),
            Ok(Err(e)) => info!("UI Automation final attempt error: {}", e),
            Err(e) => info!("UI Automation final attempt task panicked: {}", e),
        }
    }

    send_event(AppEvent::SplashBypassComplete);

    // Check if efficiency mode is enabled and minimize the window
    let efficiency_enabled = get("efficiency_mode")
        .and_then(|v| v.as_str().map(|s| s == "true"))
        .unwrap_or(false);

    if efficiency_enabled {
        info!("Efficiency mode enabled - minimizing HLL window");
        match window_focus::minimize_hll_window() {
            Ok(true) => info!("HLL window minimized"),
            Ok(false) => info!("HLL window not found for minimizing"),
            Err(e) => info!("Failed to minimize HLL window: {}", e),
        }
    }

    info!("Splash screen bypass complete");
    Ok(())
}

async fn start_seeding_inner(
    server_info: &ServerInfo,
) -> Result<(), AppError> {
    info!("Starting Seeding {}", server_info.name.as_str());
    let attempted_open_time = time::Instant::now();

    if is_game_loading() || is_process_running("HLL-Win64-Shipping.exe") {
        info!(
            "HLL is already running, exiting script - {}",
            server_info.name.as_str()
        );
        return Ok(());
    }

    // Seeding: apply efficiency mode if enabled
    open_hll(server_info, true).await.map_err(|e| AppError::new(format!("Failed to open HLL: {}", e)))?;

    let mut found_hll_running = false;
    let mut first_open_game_retry = false;
    let mut second_open_game_retry = false;

    while !found_hll_running {
        // Check if stop was requested
        if is_stop_requested() {
            info!("Stop requested during game launch - {}", server_info.name.as_str());
            return Ok(());
        }

        if is_process_running("HLL-Win64-Shipping.exe") {
            info!("Found HLL running - {}", server_info.name.as_str());
            found_hll_running = true;
        }

        if !found_hll_running && !is_game_loading() {
            // if 20 seconds has passed since open::that was called, then open::that again
            if attempted_open_time.elapsed().as_secs() > GAME_OPEN_FIRST_RETRY_SECS && !first_open_game_retry {
                if is_stop_requested() { return Ok(()); }
                info!(
                    "HLL not found open, trying again - {}",
                    server_info.name.as_str()
                );
                // Retries: don't re-apply efficiency settings (already applied on first open)
                if let Err(e) = open_hll(server_info, false).await {
                    info!("Failed to retry opening HLL: {}", e);
                }
                first_open_game_retry = true;
            }

            // if 40 seconds has passed since open::that was called, then open::that again
            if attempted_open_time.elapsed().as_secs() > GAME_OPEN_SECOND_RETRY_SECS && !second_open_game_retry {
                if is_stop_requested() { return Ok(()); }
                info!(
                    "HLL not found open, final retry - {}",
                    server_info.name.as_str()
                );
                // Retries: don't re-apply efficiency settings (already applied on first open)
                if let Err(e) = open_hll(server_info, false).await {
                    info!("Failed to retry opening HLL: {}", e);
                }
                second_open_game_retry = true;
            }

            // if 1 minute has passed since open::that was called, then exit script
            if attempted_open_time.elapsed().as_secs() > GAME_OPEN_TIMEOUT_SECS {
                info!("Error: HLL could not open - {}", server_info.name.as_str());
                return Err("Error: HLL could not open".into());
            }
        }

        if found_hll_running {
            if is_stop_requested() { return Ok(()); }
            focus_hll().await?;
        }

        // Yield to the UI event loop between process checks to prevent
        // the Windows "Application Not Responding" dialog.
        if !found_hll_running {
            sleep(Duration::from_millis(500)).await;
        }
    }

    info!(
        "Seeding started. Startup Complete. - {}",
        server_info.name.as_str()
    );

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_stop_flag_lifecycle() {
        // Start clean
        STOP_REQUESTED.store(false, Ordering::Release);
        assert!(!is_stop_requested());

        request_stop();
        assert!(is_stop_requested());

        // clear_stop resets to false (also resets CONFIG_BACKED_UP)
        clear_stop();
        assert!(!is_stop_requested());
    }

    #[test]
    fn test_snooze_lifecycle() {
        // Start clean
        clear_switch_state();

        assert!(!is_switch_snoozed());
        set_switch_snooze(300);
        assert!(is_switch_snoozed());

        let duration = take_snooze_duration();
        assert_eq!(duration, 300);
        // After take, snooze is consumed
        assert!(!is_switch_snoozed());
        assert_eq!(take_snooze_duration(), 0);

        // set_switch_snooze stores at least 1
        set_switch_snooze(0);
        assert!(is_switch_snoozed());
        clear_switch_state();
    }

    #[test]
    fn test_snooze_server_switch_clamping() {
        // Below minimum (60) → clamped to 60
        clear_switch_state();
        snooze_server_switch(10);
        assert_eq!(take_snooze_duration(), 60);

        // Above maximum (1800) → clamped to 1800
        snooze_server_switch(2000);
        assert_eq!(take_snooze_duration(), 1800);

        // Within range → unchanged
        snooze_server_switch(500);
        assert_eq!(take_snooze_duration(), 500);

        clear_switch_state();
    }

    #[test]
    fn test_switch_now_lifecycle() {
        clear_switch_state();
        assert!(!is_switch_now_requested());

        request_switch_now();
        assert!(is_switch_now_requested());

        clear_switch_state();
        assert!(!is_switch_now_requested());
    }

    #[test]
    fn test_timing_constants_sanity() {
        assert!(SPLASH_BYPASS_MIN_SECS < SPLASH_BYPASS_MAX_SECS);
        assert!(SPLASH_BYPASS_MIN_SECS > 0);
        assert!(MONITOR_MIN_INTERVAL_SECS < MONITOR_MAX_INTERVAL_SECS);
        assert!(MONITOR_BACKOFF_STEP_SECS > 0);
    }

    #[test]
    fn test_compute_stagger_no_data() {
        // No player count data → mid-range default
        let stagger = compute_stagger_secs("nonexistent_server", 50);
        assert_eq!(stagger, STAGGER_MAX_SECS / 2);
    }

    #[test]
    fn test_compute_stagger_at_threshold() {
        crate::backend::server::update_player_count("stagger_test_a", 50, 100);
        let stagger = compute_stagger_secs("stagger_test_a", 50);
        assert_eq!(stagger, STAGGER_MAX_SECS); // fill_ratio = 0 → max stagger
    }

    #[test]
    fn test_compute_stagger_full_server() {
        crate::backend::server::update_player_count("stagger_test_b", 100, 100);
        let stagger = compute_stagger_secs("stagger_test_b", 50);
        assert_eq!(stagger, 0); // fill_ratio = 1.0 → no stagger
    }

    #[test]
    fn test_compute_stagger_half_full() {
        crate::backend::server::update_player_count("stagger_test_c", 75, 100);
        let stagger = compute_stagger_secs("stagger_test_c", 50);
        // fill_ratio = 25/50 = 0.5 → stagger = 90 * 0.5 = 45
        assert_eq!(stagger, STAGGER_MAX_SECS / 2);
    }

    #[test]
    fn test_compute_stagger_nearly_full() {
        crate::backend::server::update_player_count("stagger_test_d", 90, 100);
        let stagger = compute_stagger_secs("stagger_test_d", 50);
        // fill_ratio = 40/50 = 0.8 → stagger ≈ 90 * 0.2 ≈ 18 (float truncation may vary by 1)
        assert!(stagger <= 18, "stagger {} should be <= 18 for 90/100 fill", stagger);
        assert!(stagger >= 16, "stagger {} should be >= 16 for 90/100 fill", stagger);
    }

    #[test]
    fn test_compute_stagger_below_threshold() {
        crate::backend::server::update_player_count("stagger_test_e", 30, 100);
        let stagger = compute_stagger_secs("stagger_test_e", 50);
        // below threshold → above_threshold = 0 → fill_ratio = 0 → max stagger
        assert_eq!(stagger, STAGGER_MAX_SECS);
    }
}

/// Compute the stagger delay based on how full the current server is.
/// Returns a shorter delay for fuller servers so seeders leave quickly when not needed,
/// but linger when the server is barely above the seeding threshold.
///
/// Examples (threshold=50, max=100):
///   50 players → stagger ≈ 90s (barely seeded, stay a while)
///   75 players → stagger ≈ 45s
///   90 players → stagger ≈ 18s (nearly full, leave soon)
///  100 players → stagger =  0s (full, leave immediately)
fn compute_stagger_secs(server_name: &str, threshold: i32) -> u64 {
    let (players, max_players) = match crate::backend::server::get_player_count(server_name) {
        Some(counts) => counts,
        None => return STAGGER_MAX_SECS / 2, // no data yet — use a mid-range default
    };

    let headroom = (max_players - threshold).max(1) as f64;
    let above_threshold = (players - threshold).max(0) as f64;
    let fill_ratio = (above_threshold / headroom).clamp(0.0, 1.0);

    // Scale stagger inversely with fill: full → 0s, at threshold → STAGGER_MAX_SECS
    (STAGGER_MAX_SECS as f64 * (1.0 - fill_ratio)) as u64
}

async fn monitor_loop(
    elapsed_time: std::time::Instant,
    server_info: &ServerInfo,
    region: &str,
    index: usize,
) -> Result<(), AppError> {
    // Entry check: if the candidate has already changed before we even start monitoring, exit early
    if has_candidate_changed(current_game().id, region, index, false).await {
        info!(
            "Candidate already changed at monitor start, exiting - {}",
            server_info.name.as_str()
        );
        kill_hll_process();
        sleep(Duration::from_secs(POST_KILL_WAIT_SECS)).await;
        return Ok(());
    }

    // Linear backoff state
    let mut current_interval = MONITOR_MIN_INTERVAL_SECS;
    let mut last_candidate_changed = false;

    // Dynamic stagger: delay scales inversely with server fill level.
    // Full server → leave almost immediately, barely at threshold → wait longer.
    // `switch_first_detected` tracks when we first decided to switch; the required
    // wait time is re-evaluated each iteration based on the *current* fill level,
    // so the stagger shortens automatically as the server fills up.
    let mut switch_first_detected: Option<std::time::Instant> = None;
    // Per-session random jitter so clients with identical fill don't all switch
    // at the exact same second.
    let stagger_jitter: u64 = {
        let mut rng = rand::thread_rng();
        rng.gen_range(0..=STAGGER_JITTER_MAX_SECS)
    };

    // Periodic forced HTTP refresh to catch missed SSE seeding_status events
    let mut last_forced_refresh = std::time::Instant::now();

    // Periodic status logging at INFO level for diagnostics
    let mut last_status_log = std::time::Instant::now();

    loop {
        if is_stop_requested() {
            info!("Stop requested during monitor loop - {}", server_info.name.as_str());
            break;
        }
        if !is_process_running("HLL-Win64-Shipping.exe") {
            info!("HLL is not running, stopping seeding - {}", server_info.name.as_str());
            // Set the stop flag so the frontend won't try to restart seeding.
            // This prevents the confusing loop where the app keeps trying to
            // relaunch the game after the user manually closed it.
            request_stop();
            send_event(AppEvent::HllClosed);
            break;
        }

        // Every FORCED_REFRESH_INTERVAL_SECS, bypass the SSE cache and hit the API
        // directly. This catches missed SSE events (e.g. lost during brief reconnects).
        let force_http = last_forced_refresh.elapsed().as_secs() >= FORCED_REFRESH_INTERVAL_SECS;
        if force_http {
            last_forced_refresh = std::time::Instant::now();
        }

        // Poll seeding status for candidate delta
        let candidate_changed = has_candidate_changed(current_game().id, region, index, force_http).await;

        // Adjust backoff: reset on change, otherwise linear ramp
        if candidate_changed != last_candidate_changed {
            current_interval = MONITOR_MIN_INTERVAL_SECS;
            last_candidate_changed = candidate_changed;
        } else {
            current_interval = (current_interval + MONITOR_BACKOFF_STEP_SECS).min(MONITOR_MAX_INTERVAL_SECS);
        }

        let is_timed_out = elapsed_time.elapsed().as_secs() > MAX_SEEDING_DURATION_SECS;
        let should_switch = candidate_changed || is_timed_out;

        // Manage dynamic stagger: track when switch was first needed,
        // and re-evaluate the required delay each iteration based on current fill.
        if should_switch && switch_first_detected.is_none() {
            switch_first_detected = Some(std::time::Instant::now());
            let stagger = compute_stagger_secs(&server_info.name, server_info.seeding_threshold);
            let reason = if candidate_changed { "candidate changed" } else { "time limit reached" };
            info!("Server switch needed ({}), fill-based stagger ~{}s + {}s jitter - {}",
                reason, stagger, stagger_jitter, server_info.name.as_str());
        } else if !should_switch && switch_first_detected.is_some() {
            info!("Switch conditions cleared, cancelling stagger - {}", server_info.name.as_str());
            switch_first_detected = None;
        }

        // Re-evaluate stagger each iteration: as the server fills up, the required
        // wait time shrinks so seeders leave sooner.
        let stagger_ready = switch_first_detected
            .map(|t| {
                let stagger = compute_stagger_secs(&server_info.name, server_info.seeding_threshold);
                t.elapsed().as_secs() >= stagger + stagger_jitter
            })
            .unwrap_or(false);

        if should_switch && stagger_ready {
            if is_stop_requested() { break; }

            // Server switch notification is opt-in; when disabled, kill immediately
            let switch_notification_enabled = get("switch_notification")
                .and_then(|v| v.as_str().map(|s| s == "true"))
                .unwrap_or(false);

            if !switch_notification_enabled {
                info!("Switch notification disabled, killing HLL immediately - {}", server_info.name.as_str());
                kill_hll_process();
                info!(
                    "Waiting {} seconds for HLL to close - {}",
                    POST_KILL_WAIT_SECS, server_info.name.as_str()
                );
                sleep(Duration::from_secs(POST_KILL_WAIT_SECS)).await;
                break;
            }

            info!("Starting server switch countdown - {}", server_info.name.as_str());

            // Clear any stale switch state
            clear_switch_state();

            // Focus the Esprit Seeder window
            let _ = focus_esprit_seeder();

            // Send system notification with sound alert
            crate::platform::notification::play_notification_sound();
            crate::platform::notification::show_notification(
                "Esprit Seeder",
                "Switching servers in 30s. Open Esprit Seeder to snooze or stop seeding.",
            );

            // Determine reason for switch
            let reason = if candidate_changed { "candidate_changed" } else { "time_limit" };

            // Emit server-switch-pending event
            send_event(AppEvent::ServerSwitchPending {
                countdown_secs: SERVER_SWITCH_COUNTDOWN_SECS,
                server_name: server_info.short_name.clone(),
                reason: reason.to_string(),
            });

            let mut should_resume_monitoring = false;

            // Countdown loop
            'countdown: for _ in 0..SERVER_SWITCH_COUNTDOWN_SECS {
                sleep(Duration::from_secs(1)).await;

                if is_stop_requested() {
                    send_event(AppEvent::ServerSwitchCancelled);
                    should_resume_monitoring = false;
                    break 'countdown;
                }

                if is_switch_now_requested() {
                    clear_switch_state();
                    break 'countdown;
                }

                if is_switch_snoozed() {
                    let snooze_secs = take_snooze_duration();
                    info!("Server switch snoozed for {}s - {}", snooze_secs, server_info.name.as_str());

                    send_event(AppEvent::ServerSwitchSnoozed {
                        snooze_secs,
                    });

                    // Snooze sub-loop
                    for _ in 0..snooze_secs {
                        sleep(Duration::from_secs(1)).await;

                        if is_stop_requested() {
                            send_event(AppEvent::ServerSwitchCancelled);
                            break 'countdown;
                        }

                        if is_switch_now_requested() {
                            clear_switch_state();
                            break 'countdown;
                        }
                    }

                    // Snooze expired: re-check with forced HTTP for freshest data
                    let still_changed = has_candidate_changed(current_game().id, region, index, true).await;
                    let still_timed_out = elapsed_time.elapsed().as_secs() > MAX_SEEDING_DURATION_SECS;

                    if !still_changed && !still_timed_out {
                        info!("Conditions changed after snooze, resuming monitoring - {}", server_info.name.as_str());
                        send_event(AppEvent::ServerSwitchCancelled);
                        should_resume_monitoring = true;
                    }
                    break 'countdown;
                }
            }

            if is_stop_requested() { break; }

            if should_resume_monitoring {
                clear_switch_state();
                switch_first_detected = None;
                continue;
            }

            // Proceed with kill
            send_event(AppEvent::ServerSwitchExecuting);
            info!("Executing server switch, killing HLL - {}", server_info.name.as_str());
            kill_hll_process();
            clear_switch_state();
            info!(
                "Waiting {} seconds for HLL to close - {}",
                POST_KILL_WAIT_SECS, server_info.name.as_str()
            );
            sleep(Duration::from_secs(POST_KILL_WAIT_SECS)).await;
            break;
        }

        // Periodic status log for diagnostics
        if last_status_log.elapsed().as_secs() >= STATUS_LOG_INTERVAL_SECS {
            let fill_info = crate::backend::server::get_player_count(&server_info.name)
                .map(|(p, m)| format!("{}/{}", p, m))
                .unwrap_or_else(|| "?".to_string());
            info!("Monitor: candidate_changed={}, timed_out={}, switch_pending={}, fill={}, elapsed={}m - {}",
                candidate_changed, is_timed_out, switch_first_detected.is_some(), fill_info,
                elapsed_time.elapsed().as_secs() / 60, server_info.name.as_str());
            last_status_log = std::time::Instant::now();
        }

        // Sleep with SSE reconnect interrupt — if SSE reconnects, wake immediately
        // so we re-check the candidate with fresh data (cache was invalidated on reconnect)
        tokio::select! {
            _ = tokio::time::sleep(Duration::from_secs(current_interval)) => {},
            _ = crate::api::sse::monitor_notified() => {
                info!("SSE reconnected, checking candidate immediately - {}", server_info.name.as_str());
            },
        }
    }

    // After the game exits (natural close or kill), check if HLL reset the config
    guard_config_after_close().await;

    Ok(())
}

