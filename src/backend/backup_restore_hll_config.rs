use std::fs;
use std::fs::Metadata;
use std::os::windows::fs::MetadataExt;
use log::{info, error};
use std::fs::File;
use std::io::{self, BufRead};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::SystemTime;
use once_cell::sync::Lazy;
use winapi::um::winnt::FILE_ATTRIBUTE_READONLY;

/// Cached config check result with file modification time
struct CachedConfigCheck {
    mtime: SystemTime,
    is_overwritten: bool,
}

/// Cache for is_config_overwritten - only re-scans file if mtime changed
static CONFIG_CHECK_CACHE: Lazy<Mutex<Option<CachedConfigCheck>>> =
    Lazy::new(|| Mutex::new(None));

/// Track whether efficiency mode settings have been applied this session
static EFFICIENCY_MODE_APPLIED: AtomicBool = AtomicBool::new(false);

/// Set after startup recovery so the UI can show a toast once Dioxus is ready.
/// Use `take_startup_restore_notice()` to read and clear.
static STARTUP_RESTORE_OCCURRED: AtomicBool = AtomicBool::new(false);
static STARTUP_RESTORE_FAILED: AtomicBool = AtomicBool::new(false);

/// Path to the persistent efficiency mode flag file
fn efficiency_flag_path() -> Option<PathBuf> {
    dirs::home_dir()
        .map(|p| p.join("espritseeder-backup").join("HLL").join(".efficiency_mode_active"))
}

/// Set persistent flag indicating efficiency mode is active (survives app restart)
fn set_efficiency_flag() {
    if let Some(flag_path) = efficiency_flag_path() {
        if let Some(parent) = flag_path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Err(e) = fs::write(&flag_path, "1") {
            error!("Failed to write efficiency mode flag: {:?}", e);
        } else {
            info!("Efficiency mode flag set at {:?}", flag_path);
        }
    }
}

/// Clear persistent efficiency mode flag
fn clear_efficiency_flag() {
    if let Some(flag_path) = efficiency_flag_path() {
        if flag_path.exists() {
            if let Err(e) = fs::remove_file(&flag_path) {
                error!("Failed to remove efficiency mode flag: {:?}", e);
            } else {
                info!("Efficiency mode flag cleared");
            }
        }
    }
}

/// Check if efficiency mode was left active from a previous session (app crashed/closed during seeding).
/// If detected, restores the backup and sets a flag so the UI can show a toast later.
pub fn check_and_restore_on_startup() {
    if let Some(flag_path) = efficiency_flag_path() {
        if flag_path.exists() {
            info!("Found leftover efficiency mode flag - app was closed during seeding");

            // Verify the backup exists before attempting restore
            let backup_exists = backup_path()
                .map(|p| p.join("GameUserSettings_backup.ini").exists())
                .unwrap_or(false);

            if backup_exists {
                info!("Restoring original settings from backup");
                restore_config();
                clear_efficiency_flag();
                EFFICIENCY_MODE_APPLIED.store(false, Ordering::Release);
                STARTUP_RESTORE_OCCURRED.store(true, Ordering::Release);
            } else {
                error!("Efficiency mode flag found but no backup exists - cannot restore");
                clear_efficiency_flag();
                EFFICIENCY_MODE_APPLIED.store(false, Ordering::Release);
                STARTUP_RESTORE_FAILED.store(true, Ordering::Release);
            }
        }
    }
}

/// Returns a message if a startup restore happened, clearing the flag.
/// Call this once after Dioxus is ready to show a toast.
pub fn take_startup_restore_notice() -> Option<&'static str> {
    if STARTUP_RESTORE_FAILED.compare_exchange(true, false, Ordering::AcqRel, Ordering::Acquire).is_ok() {
        return Some("Efficiency mode was left on from a previous session, but no backup was found. Your game settings may need to be reconfigured manually.");
    }
    if STARTUP_RESTORE_OCCURRED.compare_exchange(true, false, Ordering::AcqRel, Ordering::Acquire).is_ok() {
        return Some("Your game settings were restored automatically. The app was closed during seeding last session.");
    }
    None
}

fn config_path() -> Option<PathBuf> {
    dirs::data_local_dir()
        .map(|p| p.join("HLL").join("Saved").join("Config").join("WindowsNoEditor").join("GameUserSettings.ini"))
}

fn backup_path() -> Option<PathBuf> {
    dirs::home_dir()
        .map(|p| p.join("espritseeder-backup").join("HLL").join("auto"))
}

/// Invalidate the config check cache (call after restore)
pub fn invalidate_config_cache() {
    let mut cache = lock!(CONFIG_CHECK_CACHE);
    *cache = None;
}

/// Write content to a file safely: write to a temp file, sync, then rename.
/// Prevents data loss if the write is interrupted (disk full, crash, etc.)
/// because the original file is only replaced after the new content is fully on disk.
/// Cleans up the temp file on any error.
fn write_file_safe(target: &Path, content: &[u8]) -> io::Result<()> {
    let parent = target.parent().ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidInput, "target path has no parent directory")
    })?;
    let tmp_path = parent.join(format!(".hllseeder_tmp_{}", std::process::id()));

    let result = write_file_safe_inner(&tmp_path, target, content);
    if result.is_err() {
        let _ = fs::remove_file(&tmp_path);
    }
    result
}

fn write_file_safe_inner(tmp_path: &Path, target: &Path, content: &[u8]) -> io::Result<()> {
    // Write to temp file
    let file = File::create(tmp_path)?;
    let mut writer = io::BufWriter::new(file);
    std::io::Write::write_all(&mut writer, content)?;
    std::io::Write::flush(&mut writer)?;

    // sync_all ensures data is durable on disk before we rename
    let file = writer.into_inner().map_err(|e| e.into_error())?;
    file.sync_all()?;
    drop(file);

    // Rename over the target — on NTFS this replaces atomically from the
    // filesystem's perspective (no window where the target is absent/truncated)
    fs::rename(tmp_path, target)?;
    Ok(())
}

pub fn is_config_overwritten() -> bool {
    let config_path = match config_path() {
        Some(p) => p,
        None => {
            error!("Failed to determine config path");
            return true;
        }
    };

    if !config_path.exists() {
        return true;
    }

    // Get current modification time
    let current_mtime = match fs::metadata(&config_path).and_then(|m| m.modified()) {
        Ok(mtime) => mtime,
        Err(_) => {
            // Can't get mtime, fall through to scan
            return scan_config_file(&config_path);
        }
    };

    // Check cache
    {
        let cache = lock!(CONFIG_CHECK_CACHE);
        if let Some(ref cached) = *cache {
            if cached.mtime == current_mtime {
                // Cache hit - no file read needed
                return cached.is_overwritten;
            }
        }
    }

    // Cache miss - scan file and update cache
    let result = scan_config_file(&config_path);
    {
        let mut cache = lock!(CONFIG_CHECK_CACHE);
        *cache = Some(CachedConfigCheck {
            mtime: current_mtime,
            is_overwritten: result,
        });
    }
    result
}

/// Scan the config file for EULA version (the actual file read)
fn scan_config_file(config_path: &PathBuf) -> bool {
    let file = match File::open(config_path) {
        Ok(f) => f,
        Err(_) => return true,
    };
    let reader = io::BufReader::new(file);

    for line in reader.lines() {
        let line = match line {
            Ok(line) => line,
            Err(_) => continue, // Skip invalid UTF-8 lines
        };

        if line.contains("LastSeenEULAVersion=0") {
            return true;
        } else if line.contains("LastSeenEULAVersion=1") {
            return false;
        }
    }

    true
}
pub fn backup_config() {
    // Guard: if efficiency mode is active, the config file has degraded settings.
    // The existing backup contains the user's real settings — don't overwrite it.
    if EFFICIENCY_MODE_APPLIED.load(Ordering::Acquire) {
        info!("Skipping backup — efficiency mode is active, existing backup has original settings");
        return;
    }
    if let Some(flag_path) = efficiency_flag_path() {
        if flag_path.exists() {
            info!("Skipping backup — efficiency flag present, existing backup has original settings");
            return;
        }
    }

    let config_path = match config_path() {
        Some(p) => p,
        None => {
            error!("Failed to determine config path");
            return;
        }
    };
    let backup_path = match backup_path() {
        Some(p) => p,
        None => {
            error!("Failed to determine backup path");
            return;
        }
    };
    let backup_file_path = backup_path.join("GameUserSettings_backup.ini");

    info!("Starting backup process");

    // Log whether the config file exists and its metadata
    if let Ok(metadata) = fs::metadata(&config_path) {
        log_file_metadata("Config file", &metadata);
    } else {
        error!("Config file does not exist or cannot be accessed");
        return;
    }

    // Attempt to create the backup directory
    match fs::create_dir_all(&backup_path) {
        Ok(_) => info!("Backup directory ready"),
        Err(e) => {
            error!("Failed to create backup directory: {:?}", e);
            return;
        }
    }

    // Read source, write via safe helper, then verify size matches
    let content = match fs::read(&config_path) {
        Ok(c) => c,
        Err(e) => {
            error!("Failed to read config file for backup: {:?}", e);
            return;
        }
    };
    let source_len = content.len() as u64;

    if let Err(e) = write_file_safe(&backup_file_path, &content) {
        error!("Failed to write backup file: {:?}", e);
        return;
    }

    // Verify the backup was written correctly
    match fs::metadata(&backup_file_path) {
        Ok(m) if m.len() == source_len => info!("Config file successfully backed up ({} bytes)", source_len),
        Ok(m) => error!("Backup size mismatch: expected {} bytes, got {}", source_len, m.len()),
        Err(e) => error!("Failed to verify backup file: {:?}", e),
    }
}

fn log_file_metadata(name: &str, metadata: &Metadata) {
    let attributes = metadata.file_attributes();
    let is_readonly = attributes & FILE_ATTRIBUTE_READONLY != 0;

    info!(
        "{} - Size: {} bytes, Created: {:?}, Modified: {:?}, Accessed: {:?}, File Attributes: 0x{:X}",
        name,
        metadata.len(),
        metadata.created(),
        metadata.modified(),
        metadata.accessed(),
        attributes
    );

    info!(
        "{} - Permissions: ReadOnly: {}",
        name,
        is_readonly
    );
}

pub fn restore_config() {
    info!("Restoring config file from backup");
    let config_path = match config_path() {
        Some(p) => p,
        None => {
            error!("Failed to determine config path");
            return;
        }
    };
    let backup_path = match backup_path() {
        Some(p) => p,
        None => {
            error!("Failed to determine backup path");
            return;
        }
    };
    let backup_file_path = backup_path.join("GameUserSettings_backup.ini");

    if !backup_file_path.exists() {
        info!("No backup found, skipping restore");
        return;
    }

    // Read backup content
    let content = match fs::read(&backup_file_path) {
        Ok(c) => c,
        Err(e) => {
            error!("Failed to read backup file: {:?}", e);
            return;
        }
    };

    // Write via temp file + rename to avoid truncating the config on failure
    if let Err(e) = write_file_safe(&config_path, &content) {
        error!("Failed to restore config: {:?}", e);
        return;
    }

    info!("Config file restored from backup and synced to disk");
    invalidate_config_cache();
}

/// Check if efficiency mode settings have been applied
pub fn is_efficiency_mode_applied() -> bool {
    EFFICIENCY_MODE_APPLIED.load(Ordering::Acquire)
}

/// Apply efficiency mode settings to GameUserSettings.ini
/// Sets lowest possible graphics, small resolution, windowed mode, and 30 FPS cap
pub fn apply_efficiency_settings() {
    let config_path = match config_path() {
        Some(p) => p,
        None => {
            error!("Failed to determine config path for efficiency mode");
            return;
        }
    };

    if !config_path.exists() {
        error!("Config file does not exist, cannot apply efficiency settings");
        return;
    }

    // Atomically claim the flag — only one caller proceeds
    if EFFICIENCY_MODE_APPLIED.compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire).is_err() {
        info!("Efficiency settings already applied this session, skipping");
        return;
    }

    info!("Applying efficiency mode settings");

    // Read the current config
    let content = match fs::read_to_string(&config_path) {
        Ok(c) => c,
        Err(e) => {
            error!("Failed to read config file: {:?}", e);
            EFFICIENCY_MODE_APPLIED.store(false, Ordering::Release);
            return;
        }
    };

    // Apply efficiency settings
    let modified = apply_efficiency_ini_settings(&content);

    // Write-ahead: set the persistent flag BEFORE writing settings.
    // If we crash after the flag but before the write, the next startup
    // will do a harmless no-op restore from the still-original backup.
    // If we crash after the write but before the flag (old order), the
    // settings would be stuck at efficiency mode with no way to detect it.
    set_efficiency_flag();

    // Write via temp file + rename to avoid truncating the config on failure
    if let Err(e) = write_file_safe(&config_path, modified.as_bytes()) {
        error!("Failed to write efficiency settings: {:?}", e);
        clear_efficiency_flag(); // Roll back the flag since the write failed
        EFFICIENCY_MODE_APPLIED.store(false, Ordering::Release);
        return;
    }

    info!("Efficiency mode settings applied and synced to disk");
    invalidate_config_cache();
}

/// Restore original user settings after seeding (if efficiency mode was applied)
pub fn restore_after_seeding() {
    // Atomically claim the restore — only one caller proceeds
    if EFFICIENCY_MODE_APPLIED.compare_exchange(true, false, Ordering::AcqRel, Ordering::Acquire).is_err() {
        info!("Efficiency mode was not applied, skipping restore");
        return;
    }

    info!("Restoring original settings after seeding");
    restore_config();
    clear_efficiency_flag(); // Clear persistent flag since we restored successfully
}

/// Apply efficiency settings to INI content
/// Uses single-pass O(lines) processing with HashMap lookups instead of O(settings * lines)
fn apply_efficiency_ini_settings(content: &str) -> String {
    use std::collections::{HashMap, HashSet};

    let has_trailing_newline = content.ends_with('\n') || content.ends_with("\r\n");

    // Settings to apply (key, value)
    // Using 1024x768 as the minimum resolution supported by HLL
    let settings: &[(&str, &str)] = &[
        // Minimum supported resolution - 1024x768 windowed
        ("ResolutionSizeX", "1024"),
        ("ResolutionSizeY", "768"),
        ("LastUserConfirmedResolutionSizeX", "1024"),
        ("LastUserConfirmedResolutionSizeY", "768"),
        ("DesiredScreenWidth", "1024"),
        ("DesiredScreenHeight", "768"),
        ("LastUserConfirmedDesiredScreenWidth", "1024"),
        ("LastUserConfirmedDesiredScreenHeight", "768"),
        // Windowed mode (2 = windowed)
        ("FullscreenMode", "2"),
        ("LastConfirmedFullscreenMode", "2"),
        ("PreferredFullscreenMode", "2"),
        // Frame rate cap - 30 FPS (safe minimum)
        ("FrameRateLimit", "30.000000"),
        // All graphics to lowest (0)
        ("sg.ResolutionQuality", "50.000000"),
        ("sg.ViewDistanceQuality", "0"),
        ("sg.AntiAliasingQuality", "0"),
        ("sg.ShadowQuality", "0"),
        ("sg.PostProcessQuality", "0"),
        ("sg.TextureQuality", "0"),
        ("sg.EffectsQuality", "0"),
        ("sg.FoliageQuality", "0"),
        ("sg.ShadingQuality", "0"),
        // Gameplay options to reduce clutter/rendering
        ("bGoreDisabled", "True"),
        ("bShowHints", "False"),
        ("bHideKickVoteRequests", "True"),
        ("bShowCommandMessages", "False"),
        ("bShowChatForNewMessages", "False"),
        ("DeadBodiesDespawnDelay", "30"),
        // Audio settings - low quality and muted
        ("AudioQualityLevel", "0"),
        ("MasterVolume", "0.000000"),
        ("MicrophoneVolume", "0.000000"),
    ];

    // Build HashMap for O(1) key lookups (single pass instead of O(settings * lines))
    let settings_map: HashMap<&str, &str> = settings.iter().copied().collect();
    let mut found_keys: HashSet<&str> = HashSet::with_capacity(settings.len());

    // Pre-allocate result with same capacity as input
    let mut result = String::with_capacity(content.len());
    let mut first_line = true;

    for line in content.lines() {
        if !first_line {
            result.push_str("\r\n");
        }
        first_line = false;

        // Check if this line matches a setting key (split on first '=')
        if let Some(eq_pos) = line.find('=') {
            let key = &line[..eq_pos];
            if let Some(&value) = settings_map.get(key) {
                result.push_str(key);
                result.push('=');
                result.push_str(value);
                found_keys.insert(key);
                continue;
            }
        }
        result.push_str(line);
    }

    // Log any settings not found in the config
    for (key, _) in settings {
        if !found_keys.contains(key) {
            info!("Setting {}= not found in config, skipping", key);
        }
    }

    if has_trailing_newline {
        result.push_str("\r\n");
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_apply_efficiency_ini_settings_resolution() {
        let content = "ResolutionSizeX=1920\nResolutionSizeY=1080\n";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("ResolutionSizeX=1024"));
        assert!(result.contains("ResolutionSizeY=768"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_fullscreen_mode() {
        let content = "FullscreenMode=0\nLastConfirmedFullscreenMode=0\n";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("FullscreenMode=2"));
        assert!(result.contains("LastConfirmedFullscreenMode=2"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_frame_rate() {
        let content = "FrameRateLimit=144.000000\n";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("FrameRateLimit=30.000000"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_graphics_quality() {
        let content = "sg.ViewDistanceQuality=4\nsg.ShadowQuality=3\nsg.TextureQuality=4\n";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("sg.ViewDistanceQuality=0"));
        assert!(result.contains("sg.ShadowQuality=0"));
        assert!(result.contains("sg.TextureQuality=0"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_audio() {
        let content = "MasterVolume=1.000000\nAudioQualityLevel=2\n";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("MasterVolume=0.000000"));
        assert!(result.contains("AudioQualityLevel=0"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_preserves_other_settings() {
        let content = "CustomSetting=MyValue\nResolutionSizeX=1920\n";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("CustomSetting=MyValue"));
        assert!(result.contains("ResolutionSizeX=1024"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_crlf_output() {
        let content = "Setting1=A\nSetting2=B\n";
        let result = apply_efficiency_ini_settings(content);
        // Output should use Windows line endings
        assert!(result.contains("\r\n"));
        // Trailing newline should be preserved
        assert!(result.ends_with("\r\n"));
    }

    #[test]
    fn test_apply_efficiency_ini_settings_no_trailing_newline() {
        let content = "Setting1=A\nSetting2=B";
        let result = apply_efficiency_ini_settings(content);
        assert!(result.contains("\r\n"));
        // No trailing newline in input = no trailing newline in output
        assert!(result.ends_with("Setting2=B"));
    }

    #[test]
    fn test_efficiency_flag_path_not_none() {
        let path = efficiency_flag_path();
        assert!(path.is_some());
        let path = path.unwrap();
        assert!(path.to_string_lossy().contains("espritseeder-backup"));
        assert!(path.to_string_lossy().contains(".efficiency_mode_active"));
    }
}
