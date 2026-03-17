use chrono::{NaiveDate, NaiveTime, Utc, Timelike};
use log::info;
use once_cell::sync::Lazy;
use serde::Serialize;
use std::os::windows::process::CommandExt;
use std::sync::Mutex;

use crate::error::AppError;
use crate::backend::task_scheduler::setup_scheduled_task;

/// Windows flag to prevent console windows from flashing when spawning schtasks.exe
const CREATE_NO_WINDOW: u32 = 0x08000000;

/// Create a schtasks Command with CREATE_NO_WINDOW to suppress console flicker.
fn schtasks() -> std::process::Command {
    let mut cmd = std::process::Command::new("schtasks");
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd
}

fn setup_auto_seed_impl(start_time: String, task_name: &str, args: &str) -> Result<String, AppError> {
    info!("Setting up Auto Seed: {}", task_name);

    setup_scheduled_task(start_time, task_name.to_string(), true, args).map_err(|err| {
        info!("Error setting up scheduled task {}: {}", task_name, err);
        AppError::new(format!("Failed to setup scheduled task: {}", err))
    })?;

    let mut result = verify_scheduled_task(task_name)?;

    // Append power configuration warnings if any
    if let Some(warnings) = crate::backend::power::check_power_warnings() {
        result.push_str(&warnings);
    }

    Ok(result)
}

/// Verify a scheduled task exists and return its status
fn verify_scheduled_task(task_name: &str) -> Result<String, AppError> {
    let output = schtasks()
        .args(["/query", "/tn", task_name, "/fo", "LIST"])
        .output()
        .map_err(|e| AppError::new(format!("Failed to query task: {}", e)))?;

    if output.status.success() {
        let stdout: String = String::from_utf8_lossy(&output.stdout)
            .chars()
            .take(500)
            .collect();
        Ok(format!("Daily auto seed is now setup.\n\n{}\n\nYour computer will wake from sleep automatically. Make sure 'Allow wake timers' is enabled in your Windows power plan settings.", stdout))
    } else {
        Ok("There is no scheduled task present. Please try again.".to_string())
    }
}

fn uninstall_auto_seed_impl(task_names: &[&str], label: &str) -> Result<bool, AppError> {
    info!("Uninstalling auto seed ({})", label);

    for task_name in task_names {
        let output = schtasks()
            .args(["/delete", "/tn", task_name, "/f"])
            .output();

        if let Ok(result) = output {
            if result.status.success() {
                info!("Deleted scheduled task: {}", task_name);
                return Ok(true);
            }
        }
    }

    Ok(false)
}

pub async fn setup_auto_seed(start_time: String) -> Result<String, AppError> {
    setup_auto_seed_impl(start_time, "Esprit-Seeder", "--autoseed-na")
}

pub async fn setup_auto_seed_2(start_time: String) -> Result<String, AppError> {
    setup_auto_seed_impl(start_time, "Esprit-Seeder-Secondary", "--autoseed-eu")
}

pub async fn uninstall_auto_seed() -> Result<bool, AppError> {
    uninstall_auto_seed_impl(&["Esprit-Seeder"], "NA")
}

pub async fn uninstall_auto_seed_eu() -> Result<bool, AppError> {
    uninstall_auto_seed_impl(&["Esprit-Seeder-Secondary", "Esprit-Seeder-2"], "EU")
}

pub async fn is_eu_autoseed_installed() -> bool {
    let task_names = ["Esprit-Seeder-Secondary", "Esprit-Seeder-2"];
    for task_name in task_names {
        let output = schtasks()
            .args(["/query", "/tn", task_name])
            .output();
        if let Ok(result) = output {
            if result.status.success() {
                return true;
            }
        }
    }
    false
}

pub async fn view_autoseed_schedule(secondary: bool) -> Result<String, AppError> {
    let task_names: Vec<&str> = if secondary {
        vec!["Esprit-Seeder-Secondary", "Esprit-Seeder-2"]
    } else {
        vec!["Esprit-Seeder"]
    };

    for task_name in task_names {
        let output = schtasks()
            .args(["/query", "/tn", task_name, "/fo", "LIST", "/v"])
            .output();
        if let Ok(result) = output {
            if result.status.success() {
                let stdout: String = String::from_utf8_lossy(&result.stdout)
                    .chars()
                    .take(2000)
                    .collect();
                return Ok(stdout);
            }
        }
    }

    let label = if secondary { "EU" } else { "NA" };
    Ok(format!("No {} auto-seed scheduled task found.", label))
}

#[derive(Debug, Serialize)]
pub struct AutoseedStatus {
    pub na_installed: bool,
    pub na_next_run: Option<String>,
    pub eu_installed: bool,
    pub eu_next_run: Option<String>,
}

/// Parse the "Next Run Time:" value from schtasks output.
fn parse_next_run_time(output: &str) -> Option<String> {
    output.lines()
        .find_map(|line| {
            let trimmed = line.trim();
            trimmed.strip_prefix("Next Run Time:")
                .map(|rest| rest.trim().to_string())
        })
        .filter(|s| !s.is_empty() && s != "N/A")
}

fn check_task(task_names: &[&str]) -> (bool, Option<String>) {
    for task_name in task_names {
        let output = schtasks()
            .args(["/query", "/tn", task_name, "/fo", "LIST", "/v"])
            .output();
        if let Ok(result) = output {
            if result.status.success() {
                let stdout = String::from_utf8_lossy(&result.stdout);
                let next_run = parse_next_run_time(&stdout);
                return (true, next_run);
            }
        }
    }
    (false, None)
}

pub async fn get_autoseed_status() -> AutoseedStatus {
    let (na_installed, na_next_run) = check_task(&["Esprit-Seeder"]);
    let (eu_installed, eu_next_run) = check_task(&["Esprit-Seeder-Secondary", "Esprit-Seeder-2"]);
    AutoseedStatus { na_installed, na_next_run, eu_installed, eu_next_run }
}

pub async fn open_logs() -> Result<(), AppError> {
    info!("Opening Logs");
    let local_data_dir = dirs::data_local_dir()
        .ok_or_else(|| AppError::new("Could not determine local data directory"))?;
    let log_path = local_data_dir
        .join("org.espritdecorpsgaming.espritseeder")
        .join("logs");
    open::that(&log_path).map_err(|e| AppError::new(format!("Failed to open logs: {}", e)))?;

    Ok(())
}

// --- Missed autoseed detection (Modern Standby / Task Scheduler failure backup) ---

/// Tracks which regions have already triggered today to prevent double-firing.
/// Vec of (date, region) pairs; cleaned up each day.
static LAST_AUTOSEED_TRIGGER: Lazy<Mutex<Vec<(NaiveDate, String)>>> =
    Lazy::new(|| Mutex::new(Vec::new()));

/// Maximum hours past the scheduled time that a missed autoseed will still fire.
const MISSED_AUTOSEED_WINDOW_HOURS: i64 = 4;

/// Record that an autoseed was triggered for the given region today.
/// Called from setup.rs (CLI path) and events.rs (single-instance path).
pub fn record_autoseed_triggered(region: &str) {
    let today = Utc::now().date_naive();
    let mut triggers = lock!(LAST_AUTOSEED_TRIGGER);
    // Remove stale entries from previous days
    triggers.retain(|(date, _)| *date == today);
    if !triggers.iter().any(|(_, r)| r == region) {
        triggers.push((today, region.to_string()));
        info!("Recorded autoseed trigger for region '{}' on {}", region, today);
    }
}

/// Check if the given region was already triggered today.
fn was_triggered_today(region: &str) -> bool {
    let today = Utc::now().date_naive();
    let triggers = lock!(LAST_AUTOSEED_TRIGGER);
    triggers.iter().any(|(date, r)| *date == today && r == region)
}

/// Check if a scheduled task is installed (lightweight query, no verbose output).
fn is_task_installed(task_names: &[&str]) -> bool {
    for task_name in task_names {
        let output = schtasks()
            .args(["/query", "/tn", task_name])
            .output();
        if let Ok(result) = output {
            if result.status.success() {
                return true;
            }
        }
    }
    false
}

/// Autoseed slot configuration for the missed-seed checker.
struct AutoseedSlot {
    region: &'static str,
    store_key: &'static str,
    task_names: &'static [&'static str],
    cli_arg: &'static str,
}

const AUTOSEED_SLOTS: &[AutoseedSlot] = &[
    AutoseedSlot {
        region: "na",
        store_key: "auto_seed_time",
        task_names: &["Esprit-Seeder"],
        cli_arg: "--autoseed-na",
    },
    AutoseedSlot {
        region: "eu",
        store_key: "auto_seed_time_secondary",
        task_names: &["Esprit-Seeder-Secondary", "Esprit-Seeder-2"],
        cli_arg: "--autoseed-eu",
    },
];

/// Check for missed autoseeds and fire them if within the allowed window.
/// Called every 60s from the app.rs polling loop.
/// When `after_wake` is true, logs at info level for post-wake diagnostics.
/// Returns a list of regions that were triggered.
pub async fn check_missed_autoseed(after_wake: bool) -> Vec<String> {
    use crate::backend::seeding::AUTOSEED_IN_PROGRESS;
    use std::sync::atomic::Ordering;

    let mut triggered = Vec::new();

    for slot in AUTOSEED_SLOTS {
        // Skip if task isn't installed
        if !is_task_installed(slot.task_names) {
            if after_wake {
                tracing::info!(
                    "Missed autoseed check [wake] {}: task not installed",
                    slot.region
                );
            }
            continue;
        }

        // Skip if already fired today
        if was_triggered_today(slot.region) {
            if after_wake {
                tracing::info!(
                    "Missed autoseed check [wake] {}: already triggered today",
                    slot.region
                );
            }
            continue;
        }

        // Parse stored UTC time
        let utc_time_str = match crate::backend::session::get_stored_session(slot.store_key) {
            Some(t) if !t.is_empty() => t,
            _ => {
                if after_wake {
                    tracing::info!(
                        "Missed autoseed check [wake] {}: no stored time (key={})",
                        slot.region, slot.store_key
                    );
                }
                continue;
            }
        };

        let scheduled_time = match NaiveTime::parse_from_str(&utc_time_str, "%H:%M")
            .or_else(|_| NaiveTime::parse_from_str(&utc_time_str, "%H:%M:%S"))
        {
            Ok(t) => t,
            Err(e) => {
                tracing::warn!(
                    "Failed to parse stored autoseed time '{}' for {}: {}",
                    utc_time_str, slot.region, e
                );
                continue;
            }
        };

        // Build today's scheduled UTC datetime
        let now = Utc::now();
        let today = now.date_naive();
        let scheduled_dt = today.and_time(scheduled_time);
        let now_naive = now.naive_utc();

        // Check if we're within the missed window (0 to MISSED_AUTOSEED_WINDOW_HOURS past)
        let diff = now_naive.signed_duration_since(scheduled_dt);
        let hours_past = diff.num_minutes() as f64 / 60.0;

        if diff.num_seconds() < 0 {
            if after_wake {
                tracing::info!(
                    "Missed autoseed check [wake] {}: scheduled {}:{:02} UTC hasn't arrived yet (now {} UTC)",
                    slot.region, scheduled_time.hour(), scheduled_time.minute(),
                    now.format("%H:%M:%S")
                );
            }
            continue;
        }

        if diff.num_hours() >= MISSED_AUTOSEED_WINDOW_HOURS {
            if after_wake {
                tracing::info!(
                    "Missed autoseed check [wake] {}: outside window ({:.1}h past scheduled {}:{:02} UTC, max {}h)",
                    slot.region, hours_past, scheduled_time.hour(), scheduled_time.minute(),
                    MISSED_AUTOSEED_WINDOW_HOURS
                );
            }
            continue;
        }

        // Check guards: not already in progress, HLL not running
        if AUTOSEED_IN_PROGRESS.load(Ordering::Acquire) {
            tracing::info!(
                "Missed autoseed check for {}: skipping, autoseed already in progress",
                slot.region
            );
            continue;
        }

        if crate::backend::process::is_process_running("HLL-Win64-Shipping.exe") {
            tracing::info!(
                "Missed autoseed check for {}: skipping, HLL already running",
                slot.region
            );
            continue;
        }

        info!(
            "Missed autoseed detected for {} (scheduled {}:{:02} UTC, now {} UTC, {:.1}h late)",
            slot.region,
            scheduled_time.hour(),
            scheduled_time.minute(),
            now.format("%H:%M:%S"),
            hours_past
        );

        // Record the trigger first to prevent races
        record_autoseed_triggered(slot.region);

        // Fire via the single-instance path to reuse countdown + API flow
        crate::events::send_event(crate::events::AppEvent::SingleInstance {
            args: vec![slot.cli_arg.to_string()],
        });

        triggered.push(slot.region.to_string());
    }

    triggered
}

#[cfg(test)]
mod tests {
    use super::*;

    // ─── parse_next_run_time ────────────────────────────────────────

    #[test]
    fn test_parse_next_run_time_found() {
        let output = "HostName:      DESKTOP-ABC\r\n\
                      TaskName:      \\Esprit-Seeder\r\n\
                      Next Run Time: 3/15/2026 9:00:00 AM\r\n\
                      Status:        Ready\r\n";
        assert_eq!(
            parse_next_run_time(output),
            Some("3/15/2026 9:00:00 AM".to_string())
        );
    }

    #[test]
    fn test_parse_next_run_time_na() {
        let output = "Next Run Time: N/A\r\nStatus: Disabled\r\n";
        assert_eq!(parse_next_run_time(output), None);
    }

    #[test]
    fn test_parse_next_run_time_empty_value() {
        let output = "Next Run Time: \r\nStatus: Ready\r\n";
        assert_eq!(parse_next_run_time(output), None);
    }

    #[test]
    fn test_parse_next_run_time_missing_line() {
        let output = "TaskName: \\Esprit-Seeder\r\nStatus: Ready\r\n";
        assert_eq!(parse_next_run_time(output), None);
    }

    #[test]
    fn test_parse_next_run_time_empty_output() {
        assert_eq!(parse_next_run_time(""), None);
    }

    // ─── autoseed trigger tracking ──────────────────────────────────

    #[test]
    fn test_autoseed_trigger_lifecycle() {
        // Use unique region names to avoid interference with other tests
        let region1 = "test_region_lifecycle_a";
        let region2 = "test_region_lifecycle_b";

        assert!(!was_triggered_today(region1));
        assert!(!was_triggered_today(region2));

        record_autoseed_triggered(region1);
        assert!(was_triggered_today(region1));
        assert!(!was_triggered_today(region2));

        // Double-recording doesn't duplicate
        record_autoseed_triggered(region1);
        assert!(was_triggered_today(region1));
    }
}
