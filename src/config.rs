use std::collections::HashMap;
use std::fs;
use std::io;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::RwLock;
use std::time::{SystemTime, UNIX_EPOCH};

use once_cell::sync::Lazy;
use serde_json::Value;
use tracing::{error, info, warn};

/// Debounce configuration
const SAVE_DEBOUNCE_MS: u64 = 500;

/// Lock-free debounce: stores last save time as epoch milliseconds
static LAST_SAVE_MS: AtomicU64 = AtomicU64::new(0);
static SAVE_PENDING: AtomicBool = AtomicBool::new(false);

/// In-memory config store backed by a JSON file
static CONFIG: Lazy<RwLock<HashMap<String, Value>>> = Lazy::new(|| RwLock::new(HashMap::new()));

/// Path to the config file on disk
static CONFIG_PATH: Lazy<RwLock<Option<PathBuf>>> = Lazy::new(|| RwLock::new(None));

/// Validate that a Windows username contains only safe characters for use in shell commands.
fn is_safe_username(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 104
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '.' || c == '-' || c == ' ')
}

/// Write content to a file safely: write to a temp file, sync, then rename.
/// Prevents data loss if the write is interrupted (disk full, crash, etc.).
fn write_file_safe(target: &Path, content: &[u8]) -> io::Result<()> {
    let parent = target.parent().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "target path has no parent directory",
        )
    })?;
    let tmp_path = parent.join(format!("config.json.tmp_{}", std::process::id()));

    let result = write_file_safe_inner(&tmp_path, target, content);
    if result.is_err() {
        let _ = fs::remove_file(&tmp_path);
    }
    result
}

fn write_file_safe_inner(tmp_path: &Path, target: &Path, content: &[u8]) -> io::Result<()> {
    let file = fs::File::create(tmp_path)?;
    let mut writer = io::BufWriter::new(file);
    io::Write::write_all(&mut writer, content)?;
    io::Write::flush(&mut writer)?;

    let file = writer.into_inner().map_err(|e| e.into_error())?;
    file.sync_all()?;
    drop(file);

    fs::rename(tmp_path, target)?;
    Ok(())
}

/// Restrict a directory's ACL so only the current user has access.
/// Uses `icacls` to disable inheritance and grant full control to the current user only.
fn restrict_directory_acl(path: &std::path::Path) {
    let path_str = path.to_string_lossy().to_string();

    // Disable inheritance, remove inherited ACEs
    let result = std::process::Command::new("icacls")
        .args([&path_str, "/inheritance:r"])
        .creation_flags(0x08000000) // CREATE_NO_WINDOW
        .output();

    match result {
        Ok(output) if output.status.success() => {}
        Ok(output) => {
            warn!(
                "icacls /inheritance:r returned non-zero: {}",
                String::from_utf8_lossy(&output.stderr)
            );
            return;
        }
        Err(e) => {
            warn!("Failed to run icacls: {}", e);
            return;
        }
    }

    // Grant full control to current user
    let username = std::env::var("USERNAME").unwrap_or_default();
    if !is_safe_username(&username) {
        warn!("USERNAME env var is empty or contains unexpected characters — skipping ACL grant");
        return;
    }

    let grant_arg = format!("{}:F", username);
    let result = std::process::Command::new("icacls")
        .args([&path_str, "/grant:r", &grant_arg])
        .creation_flags(0x08000000) // CREATE_NO_WINDOW
        .output();

    match result {
        Ok(output) if output.status.success() => {
            info!("Config directory ACLs restricted to current user");
        }
        Ok(output) => {
            warn!(
                "icacls /grant:r returned non-zero: {}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        Err(e) => {
            warn!("Failed to run icacls grant: {}", e);
        }
    }
}

fn current_epoch_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

/// Migrate v1 config/logs from the old path to the new org path.
/// Copies config.json if the new location doesn't already have one.
fn migrate_v1_paths() {
    let old_id = "com.kyleszombathy.hllseeder";
    let new_id = "org.espritdecorpsgaming.hllseeder";

    // Migrate config (%APPDATA%)
    if let Some(config_base) = dirs::config_dir() {
        let old_dir = config_base.join(old_id);
        let new_dir = config_base.join(new_id);
        let old_config = old_dir.join("config.json");
        let new_config = new_dir.join("config.json");

        if old_config.exists() && !new_config.exists() {
            let _ = fs::create_dir_all(&new_dir);
            match fs::copy(&old_config, &new_config) {
                Ok(_) => info!("Migrated config from {:?} to {:?}", old_config, new_config),
                Err(e) => warn!("Failed to migrate config: {}", e),
            }
        }
    }

    // Migrate logs (%LOCALAPPDATA%)
    if let Some(data_base) = dirs::data_local_dir() {
        let old_logs = data_base.join(old_id).join("logs");
        let new_logs = data_base.join(new_id).join("logs");

        if old_logs.exists() && !new_logs.exists() {
            let _ = fs::create_dir_all(&new_logs);
            if let Ok(entries) = fs::read_dir(&old_logs) {
                for entry in entries.flatten() {
                    let dest = new_logs.join(entry.file_name());
                    let _ = fs::copy(entry.path(), dest);
                }
                info!("Migrated logs from {:?} to {:?}", old_logs, new_logs);
            }
        }
    }
}

/// Initialize the config system. Call once at startup.
/// Loads existing config from disk or creates a new empty config.
pub fn init_config() {
    // Migrate from v1 paths if needed
    migrate_v1_paths();

    let config_dir = match dirs::config_dir() {
        Some(path) => path,
        None => {
            // Security: temp dir is world-readable — config may be exposed to other users
            error!("Failed to get config directory — falling back to temp dir (world-readable!)");
            std::env::temp_dir()
        }
    };

    let config_dir = config_dir.join("org.espritdecorpsgaming.hllseeder");
    let config_path = config_dir.join("config.json");

    // Ensure config directory exists
    if let Err(e) = fs::create_dir_all(&config_dir) {
        error!("Failed to create config directory {:?}: {}", config_dir, e);
    }

    // Always restrict ACLs — idempotent, covers fresh directories and cases
    // where permissions were manually changed or the dir was a temp fallback.
    restrict_directory_acl(&config_dir);

    // Load existing config if present
    if config_path.exists() {
        match fs::read_to_string(&config_path) {
            Ok(content) => {
                match serde_json::from_str::<HashMap<String, Value>>(&content) {
                    Ok(data) => {
                        let mut config = CONFIG.write().unwrap_or_else(|p| p.into_inner());
                        *config = data;
                        info!("Config loaded from {:?}", config_path);
                    }
                    Err(e) => {
                        warn!("Config parse error: {:?}, starting with empty config", e);
                    }
                }
            }
            Err(e) => {
                warn!("Config read error: {:?}, starting with empty config", e);
            }
        }
    } else {
        info!("No config file found, starting with empty config");
    }

    // Store the config path for future saves
    {
        let mut path = CONFIG_PATH.write().unwrap_or_else(|p| p.into_inner());
        *path = Some(config_path);
    }

    // Re-encrypt any plaintext sensitive values from pre-encryption versions
    migrate_plaintext_secrets();
}

/// Re-encrypt any sensitive config values still stored as plaintext.
/// This handles seamless migration from pre-encryption app versions.
fn migrate_plaintext_secrets() {
    use crate::backend::crypto;

    let mut changed = false;
    {
        let mut config = CONFIG.write().unwrap_or_else(|p| p.into_inner());
        for &key in crypto::SENSITIVE_KEYS {
            if let Some(Value::String(val)) = config.get(key) {
                if !val.is_empty() && !val.starts_with("dpapi:") {
                    match crypto::encrypt(val) {
                        Ok(encrypted) => {
                            config.insert(key.to_string(), Value::String(encrypted));
                            changed = true;
                            info!("Re-encrypted plaintext config value for key '{}'", key);
                        }
                        Err(e) => {
                            warn!("Failed to re-encrypt config key '{}': {}", key, e);
                        }
                    }
                }
            }
        }
    }

    if changed {
        if let Err(e) = save_to_disk() {
            error!("Failed to persist re-encrypted config: {}", e);
        }
    }
}

/// Get a value from config by key.
pub fn get(key: &str) -> Option<Value> {
    let config = CONFIG.read().unwrap_or_else(|p| p.into_inner());
    config.get(key).cloned()
}

fn is_valid_config_key(key: &str) -> bool {
    !key.is_empty() && key.len() <= 255 && !key.contains('\0')
}

/// Set a value in config and trigger a debounced save to disk.
pub fn set<T: serde::ser::Serialize>(key: &str, value: T) -> Result<(), Box<dyn std::error::Error>> {
    if !is_valid_config_key(key) {
        error!(
            "Invalid config key: '{}' (empty, too long, or contains null bytes)",
            key
        );
        return Err("Invalid config key".into());
    }

    let json_value = serde_json::to_value(value)?;

    {
        let mut config = CONFIG.write().unwrap_or_else(|p| p.into_inner());
        config.insert(key.to_string(), json_value);
    }

    // Lock-free debounced save
    let now = current_epoch_ms();
    let should_save = LAST_SAVE_MS
        .fetch_update(Ordering::AcqRel, Ordering::Acquire, |last| {
            if now - last >= SAVE_DEBOUNCE_MS {
                Some(now)
            } else {
                None
            }
        })
        .is_ok();

    if should_save {
        SAVE_PENDING.store(false, Ordering::Release);
        save_to_disk()?;
    } else {
        SAVE_PENDING.store(true, Ordering::Release);
    }

    Ok(())
}

/// Save the in-memory config to disk.
fn save_to_disk() -> Result<(), Box<dyn std::error::Error>> {
    let path = {
        let guard = CONFIG_PATH.read().unwrap_or_else(|p| p.into_inner());
        match guard.as_ref() {
            Some(p) => p.clone(),
            None => return Err("Config path not initialized".into()),
        }
    };

    let json = {
        let config = CONFIG.read().unwrap_or_else(|p| p.into_inner());
        serde_json::to_string_pretty(&*config)?
    };

    write_file_safe(&path, json.as_bytes())?;
    Ok(())
}

/// Flush any pending config saves to disk.
/// Call this on app shutdown to ensure no data is lost.
pub fn flush_pending_saves() {
    if SAVE_PENDING.swap(false, Ordering::AcqRel) {
        if let Err(e) = save_to_disk() {
            error!("Failed to flush pending config save: {}", e);
        } else {
            info!("Flushed pending config save on shutdown");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_valid_config_key_normal() {
        assert!(is_valid_config_key("player_name"));
        assert!(is_valid_config_key("session_id"));
        assert!(is_valid_config_key("api_key"));
    }

    #[test]
    fn test_valid_config_key_single_char() {
        assert!(is_valid_config_key("a"));
        assert!(is_valid_config_key("1"));
    }

    #[test]
    fn test_valid_config_key_max_length() {
        let key = "a".repeat(255);
        assert!(is_valid_config_key(&key));
    }

    #[test]
    fn test_invalid_config_key_empty() {
        assert!(!is_valid_config_key(""));
    }

    #[test]
    fn test_invalid_config_key_too_long() {
        let key = "a".repeat(256);
        assert!(!is_valid_config_key(&key));
    }

    #[test]
    fn test_invalid_config_key_null_byte() {
        assert!(!is_valid_config_key("test\0key"));
        assert!(!is_valid_config_key("\0"));
        assert!(!is_valid_config_key("key\0"));
    }

    #[test]
    fn test_valid_config_key_special_chars() {
        assert!(is_valid_config_key("key-with-dashes"));
        assert!(is_valid_config_key("key_with_underscores"));
        assert!(is_valid_config_key("key.with.dots"));
        assert!(is_valid_config_key("key/with/slashes"));
    }

    #[test]
    fn test_valid_config_key_unicode() {
        assert!(is_valid_config_key("\u{D0A4}")); // Korean
        assert!(is_valid_config_key("\u{9375}")); // Japanese
        assert!(is_valid_config_key("cl\u{00E9}")); // French
    }

    // ─── is_safe_username tests ──────────────────────────────────────

    #[test]
    fn test_safe_username_normal() {
        assert!(is_safe_username("Alice"));
        assert!(is_safe_username("bob_smith"));
        assert!(is_safe_username("John Doe"));
        assert!(is_safe_username("user.name"));
        assert!(is_safe_username("user-name"));
    }

    #[test]
    fn test_safe_username_rejects_empty() {
        assert!(!is_safe_username(""));
    }

    #[test]
    fn test_safe_username_rejects_too_long() {
        assert!(!is_safe_username(&"a".repeat(105)));
        assert!(is_safe_username(&"a".repeat(104)));
    }

    #[test]
    fn test_safe_username_rejects_special_chars() {
        assert!(!is_safe_username("user;whoami"));
        assert!(!is_safe_username("user&calc"));
        assert!(!is_safe_username("user|dir"));
        assert!(!is_safe_username("user$(cmd)"));
        assert!(!is_safe_username("user`cmd`"));
        assert!(!is_safe_username("user\nname"));
        assert!(!is_safe_username("user\0name"));
        assert!(!is_safe_username("../admin"));
    }

    // ─── write_file_safe tests ───────────────────────────────────────

    #[test]
    fn test_write_file_safe_creates_file() {
        let dir = std::env::temp_dir().join("esprit_test_write_safe");
        let _ = fs::create_dir_all(&dir);
        let target = dir.join("test_config.json");

        let content = b"{\"key\": \"value\"}";
        write_file_safe(&target, content).unwrap();

        let read_back = fs::read_to_string(&target).unwrap();
        assert_eq!(read_back, "{\"key\": \"value\"}");

        // No temp file should remain
        let tmp = dir.join(format!("config.json.tmp_{}", std::process::id()));
        assert!(!tmp.exists());

        // Cleanup
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn test_write_file_safe_overwrites_existing() {
        let dir = std::env::temp_dir().join("esprit_test_write_safe_overwrite");
        let _ = fs::create_dir_all(&dir);
        let target = dir.join("test_config.json");

        fs::write(&target, b"old content").unwrap();
        write_file_safe(&target, b"new content").unwrap();

        let read_back = fs::read_to_string(&target).unwrap();
        assert_eq!(read_back, "new content");

        let _ = fs::remove_dir_all(&dir);
    }
}
