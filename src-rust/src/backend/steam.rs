use std::time::Duration;

use enigo::{
    Coordinate,
    Enigo, Mouse, Settings,
};
use log::info;
use once_cell::sync::{Lazy, OnceCell};
use regex::Regex;
use tokio::process::Command;
use tokio::time::sleep;
use winreg::enums::*;
use winreg::RegKey;

use crate::backend::backup_restore_hll_config::apply_efficiency_settings;
use crate::backend::game::GameDefinition;
use crate::config::get;
use crate::backend::process::is_steam_running_fresh;
use crate::backend::server::ServerInfo;
use crate::backend::seeding::check_and_restore_config;

// Cached Steam executable path - registry lookup only happens once per session
pub static STEAM_PATH_CACHE: OnceCell<std::path::PathBuf> = OnceCell::new();

// Cached regex for IP validation - compiled once on first use
static IP_REGEX: Lazy<Regex> = Lazy::new(|| {
    Regex::new(r"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(:\d{1,5})?$")
        .expect("Invalid IP regex pattern")
});

// Steam cold-start constants
const STEAM_STARTUP_POLL_INTERVAL_MS: u64 = 2000;
const STEAM_STARTUP_TIMEOUT_SECS: u64 = 120;
const STEAM_POST_STARTUP_DELAY_SECS: u64 = 5;

/// Validate a server IP address to prevent command injection.
pub fn validate_server_ip(ip: &str) -> Result<(), String> {
    if ip.len() > 21 {
        return Err("IP address too long".to_string());
    }

    if ip.contains('\0') {
        return Err("IP contains invalid characters".to_string());
    }

    let captures = IP_REGEX.captures(ip)
        .ok_or_else(|| "Invalid IP format".to_string())?;

    for i in 1..=4 {
        let octet: u16 = captures.get(i)
            .ok_or_else(|| "Invalid IP format".to_string())?
            .as_str()
            .parse()
            .map_err(|_| "Invalid octet value".to_string())?;
        if octet > 255 {
            return Err(format!("IP octet {} out of range", octet));
        }
    }

    if let Some(port_match) = captures.get(5) {
        let port_str = &port_match.as_str()[1..];
        let port: u32 = port_str.parse()
            .map_err(|_| "Invalid port value".to_string())?;
        if port == 0 || port > 65535 {
            return Err(format!("Port {} out of range", port));
        }
    }

    Ok(())
}

pub fn get_steam_executable_path() -> std::io::Result<std::path::PathBuf> {
    if let Some(path) = STEAM_PATH_CACHE.get() {
        return Ok(path.clone());
    }

    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let subkey_path = r"SOFTWARE\Wow6432Node\Valve\Steam";
    let subkey = hklm.open_subkey_with_flags(subkey_path, KEY_READ)?;

    match subkey.get_value::<String, _>("InstallPath") {
        Ok(install_path) => {
            let steam_path = std::path::PathBuf::from(&install_path).join("steam.exe");

            if !steam_path.exists() || !steam_path.is_file() {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::NotFound,
                    "Steam executable not found (check Steam installation)",
                ));
            }

            let _ = STEAM_PATH_CACHE.set(steam_path.clone());
            info!("Steam path cached: {:?}", steam_path);
            Ok(steam_path)
        }
        Err(_) => Err(std::io::Error::other(
            "Steam not found in the Windows Registry",
        )),
    }
}

/// Ensure the Steam client is running before issuing `-applaunch`.
pub async fn ensure_steam_running() -> std::io::Result<()> {
    if is_steam_running_fresh() {
        info!("Steam is already running");
        return Ok(());
    }

    info!("Steam is not running — starting Steam client");
    let steam_path = get_steam_executable_path()?;

    let mut child = Command::new(&steam_path)
        .spawn()
        .map_err(|e| std::io::Error::new(e.kind(), format!("Failed to start Steam: {}", e)))?;

    let start = tokio::time::Instant::now();
    let deadline = start + Duration::from_secs(STEAM_STARTUP_TIMEOUT_SECS);

    loop {
        sleep(Duration::from_millis(STEAM_STARTUP_POLL_INTERVAL_MS)).await;
        let elapsed_secs = start.elapsed().as_secs();

        match child.try_wait() {
            Ok(Some(status)) if !status.success() => {
                return Err(std::io::Error::other(
                    format!("Steam launcher exited prematurely with status: {}", status),
                ));
            }
            _ => {}
        }

        if is_steam_running_fresh() {
            info!("Steam client detected after {}s — waiting {}s for IPC initialization",
                elapsed_secs, STEAM_POST_STARTUP_DELAY_SECS);
            sleep(Duration::from_secs(STEAM_POST_STARTUP_DELAY_SECS)).await;
            info!("Steam is ready");
            return Ok(());
        }

        if tokio::time::Instant::now() >= deadline {
            return Err(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                format!("Steam did not start within {}s", STEAM_STARTUP_TIMEOUT_SECS),
            ));
        }

        if elapsed_secs % 10 == 0 {
            info!("Waiting for Steam to start... ({}s elapsed)", elapsed_secs);
        }
    }
}

pub async fn open_hll(server_info: &ServerInfo, apply_efficiency: bool) -> std::io::Result<()> {
    check_and_restore_config();

    ensure_steam_running().await?;

    if apply_efficiency {
        let efficiency_enabled = get("efficiency_mode")
            .and_then(|v| v.as_str().map(|s| s == "true"))
            .unwrap_or(false);

        if efficiency_enabled {
            info!("Efficiency mode enabled for seeding - applying low graphics settings");
            apply_efficiency_settings();
        }
    }

    validate_server_ip(&server_info.ip).map_err(|e| {
        std::io::Error::new(std::io::ErrorKind::InvalidInput, format!("Invalid server IP: {}", e))
    })?;

    let enigo_result = Enigo::new(&Settings::default());
    if let Ok(mut enigo) = enigo_result {
        let _ = enigo.move_mouse(700, 700, Coordinate::Abs);
    } else {
        info!("Failed to create Enigo instance for mouse move, continuing without");
    }

    info!("Opening HLL");
    let program = get_steam_executable_path()?;
    let args = &[
        "-applaunch",
        "686810",
        "-dev",
        "+connect",
        server_info.ip.as_str(),
    ];

    let mut child = Command::new(program)
        .args(args)
        .spawn()
        .map_err(|e| std::io::Error::new(e.kind(), format!("Failed to start Steam: {}", e)))?;

    match tokio::time::timeout(Duration::from_secs(30), child.wait()).await {
        Ok(Ok(_)) => info!("Steam launcher process exited normally"),
        Ok(Err(e)) => info!("Steam launcher process wait error (continuing): {}", e),
        Err(_) => {
            info!("Steam launcher process did not exit within 30s, continuing anyway");
            let _ = child.kill().await;
        }
    }

    Ok(())
}

pub async fn open_game(game: &GameDefinition, server_info: &ServerInfo, apply_efficiency: bool) -> std::io::Result<()> {
    crate::backend::seeding::check_and_restore_config();

    ensure_steam_running().await?;

    if apply_efficiency && game.supports_efficiency_mode {
        let efficiency_enabled = get("efficiency_mode")
            .and_then(|v| v.as_str().map(|s| s == "true"))
            .unwrap_or(false);

        if efficiency_enabled {
            info!("Efficiency mode enabled for seeding - applying low graphics settings");
            apply_efficiency_settings();
        }
    }

    validate_server_ip(&server_info.ip).map_err(|e| {
        std::io::Error::new(std::io::ErrorKind::InvalidInput, format!("Invalid server IP: {}", e))
    })?;

    let enigo_result = Enigo::new(&Settings::default());
    if let Ok(mut enigo) = enigo_result {
        let _ = enigo.move_mouse(700, 700, Coordinate::Abs);
    } else {
        info!("Failed to create Enigo instance for mouse move, continuing without");
    }

    info!("Opening {}", game.display_name);
    let program = get_steam_executable_path()?;
    let args = &[
        "-applaunch",
        game.steam_app_id,
        "-dev",
        "+connect",
        server_info.ip.as_str(),
    ];

    let mut child = Command::new(program)
        .args(args)
        .spawn()
        .map_err(|e| std::io::Error::new(e.kind(), format!("Failed to start Steam: {}", e)))?;

    match tokio::time::timeout(Duration::from_secs(30), child.wait()).await {
        Ok(Ok(_)) => info!("Steam launcher process exited normally"),
        Ok(Err(e)) => info!("Steam launcher process wait error (continuing): {}", e),
        Err(_) => {
            info!("Steam launcher process did not exit within 30s, continuing anyway");
            let _ = child.kill().await;
        }
    }

    Ok(())
}

/// Get Steam install path from registry (without the steam.exe suffix)
pub fn get_steam_install_path() -> std::io::Result<std::path::PathBuf> {
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let subkey_path = r"SOFTWARE\Wow6432Node\Valve\Steam";
    let subkey = hklm.open_subkey_with_flags(subkey_path, KEY_READ)?;

    let install_path: String = subkey.get_value("InstallPath")?;
    Ok(std::path::PathBuf::from(install_path))
}

/// Check if HLL is installed in a Steam library and return Movies folder path
pub fn find_hll_in_library(steamapps: &std::path::Path, app_id: &str) -> Option<String> {
    let manifest = steamapps.join(format!("appmanifest_{}.acf", app_id));
    if !manifest.exists() {
        return None;
    }

    let movies_path = steamapps
        .join("common")
        .join("Hell Let Loose")
        .join("HLL")
        .join("Content")
        .join("Movies");

    if movies_path.exists() {
        Some(movies_path.to_string_lossy().to_string())
    } else {
        let hll_path = steamapps.join("common").join("Hell Let Loose");
        if hll_path.exists() {
            Some(hll_path.to_string_lossy().to_string())
        } else {
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_valid_ip_basic() {
        assert!(validate_server_ip("192.168.1.1").is_ok());
    }

    #[test]
    fn test_valid_ip_with_port() {
        assert!(validate_server_ip("10.0.0.1:27015").is_ok());
    }

    #[test]
    fn test_valid_ip_zeros() {
        assert!(validate_server_ip("0.0.0.0").is_ok());
    }

    #[test]
    fn test_valid_ip_max_octets() {
        assert!(validate_server_ip("255.255.255.255").is_ok());
    }

    #[test]
    fn test_valid_ip_max_port() {
        assert!(validate_server_ip("1.2.3.4:65535").is_ok());
    }

    #[test]
    fn test_invalid_ip_octet_out_of_range() {
        assert!(validate_server_ip("256.1.1.1").is_err());
    }

    #[test]
    fn test_invalid_ip_port_zero() {
        assert!(validate_server_ip("1.2.3.4:0").is_err());
    }

    #[test]
    fn test_invalid_ip_port_too_high() {
        assert!(validate_server_ip("1.2.3.4:65536").is_err());
    }

    #[test]
    fn test_invalid_ip_empty() {
        assert!(validate_server_ip("").is_err());
    }

    #[test]
    fn test_invalid_ip_letters() {
        assert!(validate_server_ip("abc.def.ghi.jkl").is_err());
    }

    #[test]
    fn test_invalid_ip_too_long() {
        assert!(validate_server_ip("111.111.111.111:65535X").is_err());
    }

    #[test]
    fn test_invalid_ip_null_byte() {
        assert!(validate_server_ip("1.2.3.4\0").is_err());
    }

    #[test]
    fn test_invalid_ip_command_injection() {
        assert!(validate_server_ip("1.2.3.4; rm -rf /").is_err());
    }

    #[test]
    fn test_invalid_ip_too_few_octets() {
        assert!(validate_server_ip("1.2.3").is_err());
    }

    #[test]
    fn test_invalid_ip_trailing_dot() {
        assert!(validate_server_ip("1.2.3.4.").is_err());
    }
}
