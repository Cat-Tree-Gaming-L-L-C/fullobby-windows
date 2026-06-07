use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use log::{info, debug};

use crate::error::AppError;
use crate::backend::backup_restore_hll_config::{invalidate_config_cache, restore_config};
use crate::backend::session::validate_user_path;

/// Lightweight file info for incremental backup comparison
/// Avoids duplicate filesystem reads by extracting data from async metadata
struct FileCompareInfo {
    size: u64,
    modified: Option<std::time::SystemTime>,
}

/// Result of a single file backup operation
enum BackupFileResult {
    Copied,
    Linked,
    Failed,
}

fn backup_base_path() -> Result<std::path::PathBuf, String> {
    dirs::home_dir()
        .ok_or_else(|| "Failed to locate home directory".to_string())
        .map(|p| p.join("espritseeder-backup").join("HLL"))
}

fn manual_backup_path() -> Result<std::path::PathBuf, String> {
    backup_base_path().map(|p| p.join("manual"))
}

fn get_latest_manual_backup() -> Option<std::path::PathBuf> {
    let manual_path = manual_backup_path().ok()?;
    if !manual_path.exists() {
        return None;
    }

    std::fs::read_dir(&manual_path)
        .ok()?
        .flatten()
        .filter(|e| e.path().is_dir())
        .filter(|e| is_timestamp_folder(&e.file_name()))
        .max_by_key(|e| e.file_name())
        .map(|e| e.path())
}

/// Check if a folder name matches the backup timestamp format: YYYY-MM-DD_HH-MM-SS
/// This excludes user-renamed folders (e.g., "competitive", "cinematic") from
/// incremental backup comparisons, making the behavior more predictable.
fn is_timestamp_folder(name: &std::ffi::OsStr) -> bool {
    let name = match name.to_str() {
        Some(s) => s,
        None => return false,
    };

    // Expected format: YYYY-MM-DD_HH-MM-SS (19 characters)
    if name.len() != 19 {
        return false;
    }

    let bytes = name.as_bytes();
    // Check structure: ####-##-##_##-##-##
    bytes[4] == b'-' && bytes[7] == b'-' && bytes[10] == b'_' &&
    bytes[13] == b'-' && bytes[16] == b'-' &&
    bytes[0..4].iter().all(|b| b.is_ascii_digit()) &&
    bytes[5..7].iter().all(|b| b.is_ascii_digit()) &&
    bytes[8..10].iter().all(|b| b.is_ascii_digit()) &&
    bytes[11..13].iter().all(|b| b.is_ascii_digit()) &&
    bytes[14..16].iter().all(|b| b.is_ascii_digit()) &&
    bytes[17..19].iter().all(|b| b.is_ascii_digit())
}

pub fn has_backup() -> bool {
    get_latest_manual_backup().is_some()
}

pub fn has_auto_backup() -> bool {
    dirs::home_dir()
        .map(|p| p.join("espritseeder-backup").join("HLL").join("auto").join("GameUserSettings_backup.ini"))
        .map(|p| p.exists())
        .unwrap_or(false)
}

pub fn restore_from_auto_backup() -> Result<String, String> {
    let backup_file = dirs::home_dir()
        .ok_or_else(|| "Failed to locate home directory".to_string())?
        .join("espritseeder-backup")
        .join("HLL")
        .join("auto")
        .join("GameUserSettings_backup.ini");

    if !backup_file.exists() {
        return Err("No automatic backup found".to_string());
    }

    restore_config();
    invalidate_config_cache();
    info!("User restored game settings from automatic backup");
    Ok("Settings restored from last automatic backup.".to_string())
}

pub fn get_manual_backup_path() -> Result<String, String> {
    manual_backup_path().map(|p| p.to_string_lossy().to_string())
}

pub async fn backup_user_settings(folder_path: String) -> Result<u32, AppError> {
    use tokio::fs;
    use chrono::Local;
    use std::ffi::OsString;

    // Validate the source path (sync - uses std::fs internally for canonicalize)
    let source_path = validate_user_path(&folder_path)
        .map_err(|e| AppError::new(format!("Invalid source path: {}", e)))?;

    if !source_path.is_dir() {
        return Err("Source path is not a directory".into());
    }

    // Find the most recent backup for incremental comparison (None if home dir unavailable)
    let previous_backup = get_latest_manual_backup();

    // Pre-cache previous backup metadata to avoid per-file filesystem reads (#6 optimization)
    let prev_metadata_cache: HashMap<OsString, FileCompareInfo> = if let Some(ref prev_path) = previous_backup {
        let mut cache = HashMap::new();
        if let Ok(entries) = std::fs::read_dir(prev_path) {
            for entry in entries.flatten() {
                if let Ok(meta) = entry.metadata() {
                    if meta.is_file() {
                        cache.insert(entry.file_name(), FileCompareInfo {
                            size: meta.len(),
                            modified: meta.modified().ok(),
                        });
                    }
                }
            }
        }
        cache
    } else {
        HashMap::new()
    };

    // Collect file entries with metadata for incremental comparison
    // Uses async metadata directly to avoid duplicate filesystem reads
    let mut source_files: Vec<(std::path::PathBuf, Option<FileCompareInfo>)> = Vec::new();
    let mut entries = fs::read_dir(&source_path).await
        .map_err(|e| AppError::new(format!("Failed to read directory: {}", e)))?;

    while let Some(entry) = entries.next_entry().await
        .map_err(|e| AppError::new(format!("Failed to read entry: {}", e)))?
    {
        let path = entry.path();
        let metadata = match fs::metadata(&path).await {
            Ok(m) => m,
            Err(_) => continue,
        };
        if metadata.is_file() {
            // Extract comparison data from async metadata (no duplicate read)
            let compare_info = FileCompareInfo {
                size: metadata.len(),
                modified: metadata.modified().ok(),
            };
            source_files.push((path, Some(compare_info)));
        }
    }

    if source_files.is_empty() {
        return Err("No files found in folder.".into());
    }

    // Create dated subfolder for this backup
    let timestamp = Local::now().format("%Y-%m-%d_%H-%M-%S").to_string();
    let backup_path = manual_backup_path()
        .map_err(AppError::new)?
        .join(&timestamp);

    fs::create_dir_all(&backup_path).await
        .map_err(|e| AppError::new(format!("Failed to create backup directory: {}", e)))?;

    // Parallelize backup operations using JoinSet (#2 optimization)
    let mut join_set = tokio::task::JoinSet::new();
    let backup_path = Arc::new(backup_path);
    let previous_backup = Arc::new(previous_backup);
    let prev_metadata_cache = Arc::new(prev_metadata_cache);

    for (file_path, source_meta) in source_files {
        let backup_path = Arc::clone(&backup_path);
        let previous_backup = Arc::clone(&previous_backup);
        let prev_metadata_cache = Arc::clone(&prev_metadata_cache);

        join_set.spawn(async move {
            let file_name = match file_path.file_name() {
                Some(name) => name.to_os_string(),
                None => return BackupFileResult::Failed,
            };

            // TOCTOU mitigation: Use symlink_metadata to detect symlinks
            let symlink_meta = match tokio::fs::symlink_metadata(&file_path).await {
                Ok(m) => m,
                Err(_) => return BackupFileResult::Failed,
            };

            // Skip symlinks - only backup regular files
            if symlink_meta.file_type().is_symlink() {
                return BackupFileResult::Failed;
            }

            let dest_path = backup_path.join(&file_name);

            // Check if file is unchanged from previous backup using cached metadata
            let unchanged = is_file_unchanged_cached(&source_meta, &prev_metadata_cache, &file_name);

            if unchanged {
                // Try to hard-link unchanged file to save disk space
                // Security note: Hard links create file aliasing - both paths point to same data.
                // This is acceptable here because:
                // 1. Backup directory is user-owned (same as source)
                // 2. Files are config data, not executable code
                // 3. Disk space savings outweigh minimal risk
                // 4. Hard links are verified to point within the validated backup directory
                if let Some(ref prev_path) = *previous_backup {
                    let prev_file = prev_path.join(&file_name);
                    // Verify previous file is still a regular file (not replaced with symlink)
                    if let Ok(prev_meta) = std::fs::symlink_metadata(&prev_file) {
                        if !prev_meta.file_type().is_symlink()
                            && std::fs::hard_link(&prev_file, &dest_path).is_ok() {
                            return BackupFileResult::Linked;
                        }
                    }
                    debug!("Hard link failed - falling back to copy");
                }
            }

            // Copy the file (new, changed, or hard link failed)
            match fs::copy(&file_path, &dest_path).await {
                Ok(_) => {
                    debug!("Backed up file ({})", if unchanged { "link failed" } else { "changed" });
                    BackupFileResult::Copied
                }
                Err(e) => {
                    debug!("Failed to backup file: {}", e);
                    BackupFileResult::Failed
                }
            }
        });
    }

    // Collect results
    let mut copied_count: u32 = 0;
    let mut linked_count: u32 = 0;
    while let Some(result) = join_set.join_next().await {
        match result {
            Ok(BackupFileResult::Copied) => copied_count += 1,
            Ok(BackupFileResult::Linked) => linked_count += 1,
            Ok(BackupFileResult::Failed) | Err(_) => {}
        }
    }

    let total = copied_count + linked_count;
    if linked_count > 0 {
        info!(
            "Incremental backup complete: {} changed, {} unchanged (hard-linked), {} total",
            copied_count, linked_count, total
        );
    } else {
        info!("Backed up {} config files to {:?}", total, backup_path);
    }

    Ok(total)
}

/// Check if a source file is unchanged using pre-cached previous backup metadata.
/// Avoids per-file filesystem reads by using the metadata cache.
fn is_file_unchanged_cached(
    source_info: &Option<FileCompareInfo>,
    prev_metadata_cache: &HashMap<std::ffi::OsString, FileCompareInfo>,
    file_name: &std::ffi::OsStr,
) -> bool {
    let source_info = match source_info {
        Some(si) => si,
        None => return false,
    };

    let prev_info = match prev_metadata_cache.get(file_name) {
        Some(pi) => pi,
        None => return false,
    };

    // Compare size
    if source_info.size != prev_info.size {
        return false;
    }

    // Compare modification time with tolerance (filesystem precision varies)
    match (&source_info.modified, &prev_info.modified) {
        (Some(s), Some(p)) => {
            // Allow 2-second tolerance for filesystem time precision differences
            let diff = if *s > *p {
                s.duration_since(*p).unwrap_or(Duration::from_secs(999))
            } else {
                p.duration_since(*s).unwrap_or(Duration::from_secs(999))
            };
            diff.as_secs() < 2
        }
        _ => false,
    }
}

pub async fn restore_user_settings(backup_folder: String, dest_folder: String) -> Result<u32, AppError> {
    use tokio::fs;

    // Validate both paths (sync - uses std::fs internally for canonicalize)
    let dest_path = validate_user_path(&dest_folder)
        .map_err(|e| AppError::new(format!("Invalid destination path: {}", e)))?;

    if !dest_path.is_dir() {
        return Err("Destination path is not a directory".into());
    }

    let backup_path = validate_user_path(&backup_folder)
        .map_err(|e| AppError::new(format!("Invalid backup path: {}", e)))?;

    if !backup_path.is_dir() {
        return Err("Backup path is not a directory".into());
    }

    let mut restored_count: u32 = 0;

    let mut entries = fs::read_dir(&backup_path).await
        .map_err(|e| AppError::new(format!("Failed to read backup directory: {}", e)))?;

    while let Some(entry) = entries.next_entry().await
        .map_err(|e| AppError::new(format!("Failed to read entry: {}", e)))?
    {
        let file_path = entry.path();

        // TOCTOU mitigation: Re-validate file path is within validated backup directory
        let canonical_file = match file_path.canonicalize() {
            Ok(p) => p,
            Err(_) => continue,
        };
        if !canonical_file.starts_with(&backup_path) {
            debug!("Skipping file outside backup directory");
            continue;
        }

        // Use symlink_metadata to detect symlinks (doesn't follow them)
        let metadata = match fs::symlink_metadata(&file_path).await {
            Ok(m) => m,
            Err(_) => continue,
        };

        // Skip symlinks - only restore regular files
        if metadata.file_type().is_symlink() {
            debug!("Skipping symlink in restore");
            continue;
        }

        if metadata.is_file() {
            if let Some(file_name) = file_path.file_name() {
                let target_path = dest_path.join(file_name);

                // TOCTOU mitigation: Verify target is within destination directory
                // Use the parent + filename pattern to avoid canonicalizing non-existent file
                if let Some(parent) = target_path.parent() {
                    let canonical_parent = match parent.canonicalize() {
                        Ok(p) => p,
                        Err(_) => continue,
                    };
                    if !canonical_parent.starts_with(&dest_path) {
                        debug!("Skipping file - target outside destination");
                        continue;
                    }
                }

                match fs::copy(&file_path, &target_path).await {
                    Ok(_) => {
                        debug!("Restored config file");
                        restored_count += 1;
                    }
                    Err(e) => {
                        debug!("Failed to restore file: {}", e);
                    }
                }
            }
        }
    }

    info!("Restored {} config files from backup", restored_count);
    Ok(restored_count)
}

#[cfg(test)]
mod backup_tests {
    use super::*;
    use std::ffi::OsString;
    use std::time::{Duration, SystemTime};

    // ========== is_timestamp_folder tests ==========

    #[test]
    fn timestamp_folder_valid() {
        assert!(is_timestamp_folder(&OsString::from("2024-01-15_08-30-00")));
        assert!(is_timestamp_folder(&OsString::from("2099-12-31_23-59-59")));
    }

    #[test]
    fn timestamp_folder_wrong_length() {
        assert!(!is_timestamp_folder(&OsString::from("2024-01-15_08-30")));
        assert!(!is_timestamp_folder(&OsString::from("2024-01-15_08-30-00-extra")));
    }

    #[test]
    fn timestamp_folder_wrong_separators() {
        assert!(!is_timestamp_folder(&OsString::from("2024/01/15_08-30-00")));
        assert!(!is_timestamp_folder(&OsString::from("2024-01-15-08-30-00")));
        assert!(!is_timestamp_folder(&OsString::from("2024-01-15T08-30-00")));
    }

    #[test]
    fn timestamp_folder_non_digit_chars() {
        assert!(!is_timestamp_folder(&OsString::from("abcd-01-15_08-30-00")));
        assert!(!is_timestamp_folder(&OsString::from("2024-ab-15_08-30-00")));
    }

    #[test]
    fn timestamp_folder_user_named() {
        assert!(!is_timestamp_folder(&OsString::from("competitive")));
        assert!(!is_timestamp_folder(&OsString::from("cinematic-settings")));
        assert!(!is_timestamp_folder(&OsString::from("my-backup-2024-v2")));
    }

    // ========== is_file_unchanged_cached tests ==========

    fn make_source(size: u64, modified: SystemTime) -> Option<FileCompareInfo> {
        Some(FileCompareInfo { size, modified: Some(modified) })
    }

    fn make_cache(entries: Vec<(&str, u64, SystemTime)>) -> HashMap<OsString, FileCompareInfo> {
        entries.into_iter().map(|(name, size, modified)| {
            (OsString::from(name), FileCompareInfo { size, modified: Some(modified) })
        }).collect()
    }

    #[test]
    fn unchanged_matching_files() {
        let now = SystemTime::now();
        let source = make_source(1024, now);
        let cache = make_cache(vec![("file.ini", 1024, now)]);
        assert!(is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_size_mismatch() {
        let now = SystemTime::now();
        let source = make_source(2048, now);
        let cache = make_cache(vec![("file.ini", 1024, now)]);
        assert!(!is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_within_tolerance() {
        let now = SystemTime::now();
        let one_sec_ago = now - Duration::from_secs(1);
        let source = make_source(1024, now);
        let cache = make_cache(vec![("file.ini", 1024, one_sec_ago)]);
        assert!(is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_beyond_tolerance() {
        let now = SystemTime::now();
        let three_sec_ago = now - Duration::from_secs(3);
        let source = make_source(1024, now);
        let cache = make_cache(vec![("file.ini", 1024, three_sec_ago)]);
        assert!(!is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_missing_source() {
        let now = SystemTime::now();
        let cache = make_cache(vec![("file.ini", 1024, now)]);
        assert!(!is_file_unchanged_cached(&None, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_missing_prev() {
        let now = SystemTime::now();
        let source = make_source(1024, now);
        let cache: HashMap<OsString, FileCompareInfo> = HashMap::new();
        assert!(!is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_missing_timestamps() {
        let source = Some(FileCompareInfo { size: 1024, modified: None });
        let mut cache = HashMap::new();
        cache.insert(OsString::from("file.ini"), FileCompareInfo { size: 1024, modified: None });
        assert!(!is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }

    #[test]
    fn unchanged_one_timestamp_missing() {
        let now = SystemTime::now();
        let source = make_source(1024, now);
        let mut cache = HashMap::new();
        cache.insert(OsString::from("file.ini"), FileCompareInfo { size: 1024, modified: None });
        assert!(!is_file_unchanged_cached(&source, &cache, &OsString::from("file.ini")));
    }
}
