use std::os::windows::process::CommandExt;

/// Windows flag to prevent console windows from flashing when spawning powercfg.exe
const CREATE_NO_WINDOW: u32 = 0x08000000;

/// Run powercfg with the given arguments and return stdout as a String.
fn run_powercfg(args: &[&str]) -> Result<String, String> {
    let output = std::process::Command::new("powercfg")
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|e| format!("Failed to run powercfg: {}", e))?;

    if !output.status.success() {
        return Err(format!(
            "powercfg exited with status {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr)
        ));
    }

    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

/// Parse the output of `powercfg /a` for Modern Standby support.
fn parse_modern_standby_output(output: &str) -> bool {
    output.contains("S0 Low Power Idle")
}

/// Parse the output of `powercfg /q SCHEME_CURRENT SUB_SLEEP RTCWAKE` for wake timer status.
fn parse_wake_timers_output(output: &str) -> bool {
    output.lines().any(|line| {
        let trimmed = line.trim();
        trimmed.starts_with("Current AC Power Setting Index:")
            && trimmed.contains("0x00000001")
    })
}

/// Compose user-facing warning text from power configuration flags.
/// Returns `None` if no warnings are needed.
fn compose_power_warnings(modern_standby: bool, wake_timers_enabled: bool) -> Option<String> {
    let mut warnings = Vec::new();

    if !wake_timers_enabled {
        warnings.push(
            "WARNING: 'Allow wake timers' is DISABLED in your current power plan. \
             The scheduled task will NOT be able to wake your PC from sleep. \
             Enable it in: Power Options > Change plan settings > Change advanced power settings > Sleep > Allow wake timers."
        );
    }

    if modern_standby {
        warnings.push(
            "NOTE: Your PC uses Modern Standby (S0 Low Power Idle). \
             Task Scheduler's wake-from-sleep may be unreliable on Modern Standby systems. \
             As a backup, this app will automatically detect and fire missed autoseeds \
             when it is running."
        );
    }

    if warnings.is_empty() {
        None
    } else {
        Some(format!("\n\n{}", warnings.join("\n\n")))
    }
}

/// Detect Modern Standby (S0 Low Power Idle) by checking `powercfg /a`.
/// Returns `false` (safe default) on failure.
pub fn is_modern_standby() -> bool {
    match run_powercfg(&["/a"]) {
        Ok(output) => {
            let result = parse_modern_standby_output(&output);
            tracing::info!("is_modern_standby: {} (S0 Low Power Idle present in powercfg /a)", result);
            result
        }
        Err(e) => {
            tracing::warn!("Failed to detect Modern Standby: {}", e);
            false
        }
    }
}

/// Check if wake timers are enabled in the active power plan.
/// Runs `powercfg /q SCHEME_CURRENT SUB_SLEEP RTCWAKE` and parses the AC power setting.
/// Returns `true` (safe default) on failure.
pub fn are_wake_timers_enabled() -> bool {
    match run_powercfg(&["/q", "SCHEME_CURRENT", "SUB_SLEEP", "RTCWAKE"]) {
        Ok(output) => {
            let enabled = parse_wake_timers_output(&output);
            tracing::info!("are_wake_timers_enabled: {}", enabled);
            enabled
        }
        Err(e) => {
            tracing::warn!("Failed to check wake timers: {}", e);
            true // safe default: assume enabled
        }
    }
}

/// Compose user-facing warning text about power configuration issues.
/// Returns `None` if no warnings are needed.
pub fn check_power_warnings() -> Option<String> {
    let modern_standby = is_modern_standby();
    let wake_timers = are_wake_timers_enabled();
    compose_power_warnings(modern_standby, wake_timers)
}

#[cfg(test)]
mod tests {
    use super::*;

    // ─── parse_modern_standby_output ────────────────────────────────

    #[test]
    fn test_modern_standby_present() {
        let output = "The following sleep states are available on this system:\r\n\
                      Standby (S0 Low Power Idle) Network Connected\r\n\
                      Hibernate\r\n";
        assert!(parse_modern_standby_output(output));
    }

    #[test]
    fn test_modern_standby_absent() {
        let output = "The following sleep states are available on this system:\r\n\
                      Standby (S3)\r\n\
                      Hibernate\r\n";
        assert!(!parse_modern_standby_output(output));
    }

    #[test]
    fn test_modern_standby_empty() {
        assert!(!parse_modern_standby_output(""));
    }

    // ─── parse_wake_timers_output ───────────────────────────────────

    #[test]
    fn test_wake_timers_enabled() {
        let output = "Power Setting GUID: bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d  (Allow wake timers)\r\n\
                      Current AC Power Setting Index: 0x00000001\r\n\
                      Current DC Power Setting Index: 0x00000002\r\n";
        assert!(parse_wake_timers_output(output));
    }

    #[test]
    fn test_wake_timers_disabled() {
        let output = "Power Setting GUID: bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d  (Allow wake timers)\r\n\
                      Current AC Power Setting Index: 0x00000000\r\n\
                      Current DC Power Setting Index: 0x00000000\r\n";
        assert!(!parse_wake_timers_output(output));
    }

    #[test]
    fn test_wake_timers_empty() {
        assert!(!parse_wake_timers_output(""));
    }

    // ─── compose_power_warnings ─────────────────────────────────────

    #[test]
    fn test_warnings_no_issues() {
        assert!(compose_power_warnings(false, true).is_none());
    }

    #[test]
    fn test_warnings_wake_timers_disabled() {
        let result = compose_power_warnings(false, false).unwrap();
        assert!(result.contains("Allow wake timers"));
        assert!(!result.contains("Modern Standby"));
    }

    #[test]
    fn test_warnings_modern_standby() {
        let result = compose_power_warnings(true, true).unwrap();
        assert!(result.contains("Modern Standby"));
        assert!(!result.contains("Allow wake timers"));
    }

    #[test]
    fn test_warnings_both_issues() {
        let result = compose_power_warnings(true, false).unwrap();
        assert!(result.contains("Allow wake timers"));
        assert!(result.contains("Modern Standby"));
    }
}
