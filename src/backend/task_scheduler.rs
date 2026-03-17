use log::info;
use planif::enums::TaskCreationFlags;
use planif::schedule_builder::{Action, ScheduleBuilder};
use planif::settings::Settings;
use chrono::Local;
use once_cell::sync::Lazy;
use regex::Regex;
/// Regex for validating time input format: HH:MM or HH:MM:SS
static TIME_REGEX: Lazy<Regex> = Lazy::new(|| {
    Regex::new(r"^([01]?[0-9]|2[0-3]):([0-5][0-9])(?::([0-5][0-9]))?$")
        .expect("Invalid time regex pattern")
});

/// Validate and normalize start_time input to prevent injection attacks.
/// Accepts HH:MM or HH:MM:SS format, returns normalized HH:MM:SS.
fn validate_start_time(start_time: &str) -> Result<String, String> {
    // Check for null bytes and reasonable length
    if start_time.contains('\0') || start_time.len() > 8 {
        return Err("Invalid time format".to_string());
    }

    let captures = TIME_REGEX.captures(start_time)
        .ok_or_else(|| "Time must be in HH:MM or HH:MM:SS format".to_string())?;

    let hours: u8 = captures.get(1)
        .ok_or("Missing hours")?.as_str().parse()
        .map_err(|_| "Invalid hours".to_string())?;
    let minutes: u8 = captures.get(2)
        .ok_or("Missing minutes")?.as_str().parse()
        .map_err(|_| "Invalid minutes".to_string())?;
    let seconds: u8 = captures.get(3)
        .and_then(|m| m.as_str().parse().ok())
        .unwrap_or(0);

    // Return normalized format
    Ok(format!("{:02}:{:02}:{:02}", hours, minutes, seconds))
}

pub fn setup_scheduled_task(
    start_time: String,
    name: String,
    enabled: bool,
    args: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    // Validate start_time input before use
    let validated_time = validate_start_time(&start_time)
        .map_err(|e| format!("Invalid start time '{}': {}", start_time, e))?;

    // Point the scheduled action directly at the running exe
    let exe_path = std::env::current_exe()
        .map_err(|e| format!("Failed to get current exe path: {}", e))?;
    let app_dir = exe_path.parent()
        .ok_or("Failed to get parent directory")?;

    let exe_path_str = exe_path.to_str()
        .ok_or("Exe path contains invalid UTF-8")?
        .to_string();
    let app_dir_str = app_dir.to_str()
        .ok_or("App directory path contains invalid UTF-8")?
        .to_string();

    // Use today's date with validated time
    let today = Local::now().format("%Y-%m-%d").to_string();
    let start = format!("{}T{}", today, validated_time);

    info!(
        "Setting up scheduled task to run at {} with trigger enabled - {}",
        start, enabled
    );

    // Run on a dedicated thread so COM initializes with its own apartment.
    // The main thread is already STA (WebView2/Dioxus), which conflicts with
    // the planif ScheduleBuilder COM initialization.
    let args = args.to_string();
    let handle = std::thread::spawn(move || -> Result<(), String> {
        let sb = ScheduleBuilder::new()
            .map_err(|e| format!("Failed to create schedule builder: {}", e))?;
        let mut settings = Settings::new();
        settings.wake_to_run = Some(true);
        settings.start_when_available = Some(true);
        settings.disallow_start_if_on_batteries = Some(false);
        settings.stop_if_going_on_batteries = Some(false);

        sb.create_daily()
            .trigger(&name, enabled).map_err(|e| e.to_string())?
            .settings(settings).map_err(|e| e.to_string())?
            .days_interval(1).map_err(|e| e.to_string())?
            .action(Action::new(
                "esprit-seeder",
                &exe_path_str,
                &app_dir_str,
                &args,
            )).map_err(|e| e.to_string())?
            .start_boundary(&start).map_err(|e| e.to_string())?
            .build().map_err(|e| e.to_string())?
            .register(&name, TaskCreationFlags::CreateOrUpdate as i32).map_err(|e| e.to_string())?;

        Ok(())
    });

    handle.join()
        .map_err(|_| "Task scheduler thread panicked")?
        .map_err(|e| e.into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_validate_start_time_hhmm() {
        assert_eq!(validate_start_time("14:30").unwrap(), "14:30:00");
    }

    #[test]
    fn test_validate_start_time_hhmmss() {
        assert_eq!(validate_start_time("14:30:45").unwrap(), "14:30:45");
    }

    #[test]
    fn test_validate_start_time_single_digit_hour() {
        assert_eq!(validate_start_time("0:00").unwrap(), "00:00:00");
        assert_eq!(validate_start_time("9:05").unwrap(), "09:05:00");
    }

    #[test]
    fn test_validate_start_time_max_valid() {
        assert_eq!(validate_start_time("23:59:59").unwrap(), "23:59:59");
    }

    #[test]
    fn test_validate_start_time_invalid_hour() {
        assert!(validate_start_time("25:00").is_err());
    }

    #[test]
    fn test_validate_start_time_invalid_minutes() {
        assert!(validate_start_time("14:60").is_err());
    }

    #[test]
    fn test_validate_start_time_null_byte() {
        assert!(validate_start_time("14:30\0").is_err());
    }

    #[test]
    fn test_validate_start_time_too_long() {
        assert!(validate_start_time("123:45:67").is_err());
    }

    #[test]
    fn test_validate_start_time_empty() {
        assert!(validate_start_time("").is_err());
    }

    #[test]
    fn test_validate_start_time_garbage() {
        assert!(validate_start_time("not-a-time").is_err());
    }
}
