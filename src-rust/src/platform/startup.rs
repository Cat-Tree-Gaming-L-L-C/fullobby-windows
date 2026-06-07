use tracing::{info, warn};
use winreg::enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE};
use winreg::RegKey;

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const VALUE_NAME: &str = "EspritSeeder";

fn run_key_read() -> Option<RegKey> {
    RegKey::predef(HKEY_CURRENT_USER).open_subkey_with_flags(RUN_KEY, KEY_READ).ok()
}

fn run_key_write() -> Option<RegKey> {
    RegKey::predef(HKEY_CURRENT_USER).open_subkey_with_flags(RUN_KEY, KEY_WRITE).ok()
}

/// Check whether the "Start with Windows" registry entry exists.
pub fn is_startup_enabled() -> bool {
    run_key_read()
        .and_then(|key| key.get_value::<String, _>(VALUE_NAME).ok())
        .is_some()
}

/// Add the current exe to the Run registry key so it launches at login.
pub fn enable_startup() -> Result<(), String> {
    let exe = std::env::current_exe()
        .map_err(|e| format!("Failed to get current exe path: {}", e))?;
    let path = format!("\"{}\"", exe.display());

    let key = run_key_write().ok_or_else(|| "Failed to open Run registry key for writing".to_string())?;
    key.set_value(VALUE_NAME, &path)
        .map_err(|e| format!("Failed to write registry value: {}", e))?;

    info!("Startup enabled: {}", path);
    Ok(())
}

/// Remove the registry entry so the app no longer launches at login.
pub fn disable_startup() -> Result<(), String> {
    let key = run_key_write().ok_or_else(|| "Failed to open Run registry key for writing".to_string())?;
    match key.delete_value(VALUE_NAME) {
        Ok(()) => {
            info!("Startup disabled");
            Ok(())
        }
        Err(e) => {
            // If the value doesn't exist, that's fine
            if e.kind() == std::io::ErrorKind::NotFound {
                Ok(())
            } else {
                Err(format!("Failed to delete registry value: {}", e))
            }
        }
    }
}

/// If startup is enabled, re-write the path to the current exe.
/// Handles NSIS updates that change the exe location.
pub fn update_startup_path_if_needed() {
    let stored = match run_key_read().and_then(|key| key.get_value::<String, _>(VALUE_NAME).ok()) {
        Some(v) => v,
        None => return, // Not enabled, nothing to do
    };

    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(e) => {
            warn!("Cannot update startup path: {}", e);
            return;
        }
    };
    let expected = format!("\"{}\"", exe.display());

    if stored != expected {
        info!("Updating startup path: {} -> {}", stored, expected);
        if let Err(e) = enable_startup() {
            warn!("Failed to update startup path: {}", e);
        }
    }
}
