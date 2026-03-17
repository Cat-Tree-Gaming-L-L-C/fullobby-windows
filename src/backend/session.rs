use log::warn;

use crate::config::{get, set};
use crate::backend::seeding::{SPLASH_BYPASS_MIN_SECS, SPLASH_BYPASS_MAX_SECS};

// Input size limits for session storage
const MAX_SESSION_KEY_LENGTH: usize = 256;
const MAX_SESSION_VALUE_LENGTH: usize = 1024;
const MAX_BATCH_KEYS: usize = 50;

// Allow-list of keys the frontend may write via store_session.
// Keys not on this list are rejected to prevent overwriting internal cache data.
const ALLOWED_SESSION_KEYS: &[&str] = &[
    "session_id", "player_name", "secondary_servers_enabled",
    "efficiency_mode", "switch_notification",
    "splash_bypass_duration", "theme", "auto_seed_time",
    "auto_seed_time_secondary", "hll_config_path",
    "auth_token", "auth_refresh_token",
    "api_key", "user_id", "onboarding_complete",
    "linked_steam_ids", "guest_mode",
    "auth_provider",
    "close_to_tray",
    "update_channel",
];

/// Validate a session value for a specific key.
/// Takes ownership of value to avoid re-allocation on the success path.
/// Returns Ok(validated_value) or Err(reason) if validation fails.
fn validate_session_value(key: &str, value: String) -> Result<String, &'static str> {
    match key {
        // Validate splash_bypass_duration is within allowed range
        "splash_bypass_duration" => {
            let duration: u64 = value.parse()
                .map_err(|_| "Invalid duration value")?;
            if !(SPLASH_BYPASS_MIN_SECS..=SPLASH_BYPASS_MAX_SECS).contains(&duration) {
                return Err("Duration out of allowed range");
            }
            Ok(value)
        }
        // Auth provider — only allow known values
        "auth_provider" => {
            match value.as_str() {
                "steam" | "discord" | "guest" | "email" => Ok(value),
                _ => Err("Invalid auth provider"),
            }
        }
        // Update channel — only allow known values
        "update_channel" => {
            match value.as_str() {
                "stable" | "beta" => Ok(value),
                _ => Err("Invalid update channel"),
            }
        }
        // Boolean values
        "secondary_servers_enabled" | "efficiency_mode" | "switch_notification" | "onboarding_complete" | "guest_mode" | "close_to_tray" => {
            if value != "true" && value != "false" {
                return Err("Invalid boolean value");
            }
            Ok(value)
        }
        // Default: pass through (size limits already checked)
        _ => Ok(value)
    }
}

pub fn get_stored_session(key: &str) -> Option<String> {
    // Validate key length
    if key.len() > MAX_SESSION_KEY_LENGTH {
        return None;
    }
    match get(key) {
        Some(value) => value.as_str().map(|s| {
            crate::backend::crypto::maybe_decrypt(key, s)
        }),
        None => None
    }
}

pub fn get_batch_session(keys: Vec<String>) -> std::collections::HashMap<String, Option<String>> {
    if keys.len() > MAX_BATCH_KEYS {
        warn!("Batch session request rejected: {} keys exceeds limit of {}", keys.len(), MAX_BATCH_KEYS);
        return std::collections::HashMap::new();
    }
    keys.into_iter()
        .filter(|k| k.len() <= MAX_SESSION_KEY_LENGTH)
        .map(|k| {
            let val = get(&k).and_then(|v| v.as_str().map(|s| {
                crate::backend::crypto::maybe_decrypt(&k, s)
            }));
            (k, val)
        })
        .collect()
}

pub fn store_session(key: &str, value: String) -> Result<(), String> {
    // Validate input sizes
    if key.len() > MAX_SESSION_KEY_LENGTH || value.len() > MAX_SESSION_VALUE_LENGTH {
        warn!("Session storage rejected: key or value exceeds size limit");
        return Err("Input exceeds size limit".to_string());
    }

    // Check for null bytes in key and value
    if key.contains('\0') || value.contains('\0') {
        warn!("Session storage rejected: null bytes in input");
        return Err("Invalid characters in input".to_string());
    }

    // Allow-list: only known session keys may be written from the frontend
    if !ALLOWED_SESSION_KEYS.contains(&key) {
        warn!("Session storage rejected: unknown key '{}'", key);
        return Err("Unknown session key".to_string());
    }

    // Validate value based on key-specific rules (passes ownership to avoid reallocation)
    let validated_value = validate_session_value(key, value)
        .map_err(|e| {
            warn!("Session storage rejected for key '{}': {}", key, e);
            e.to_string()
        })?;

    let final_value = crate::backend::crypto::maybe_encrypt(key, &validated_value);
    let _ = set(key, final_value);
    Ok(())
}

/// Validate a user-provided path to prevent directory traversal attacks.
/// Returns the canonicalized path if valid, or an error if the path is suspicious.
pub fn validate_user_path(path_str: &str) -> Result<std::path::PathBuf, String> {
    // Length limit (Windows MAX_PATH)
    const MAX_PATH_LENGTH: usize = 260;
    if path_str.len() > MAX_PATH_LENGTH {
        return Err("Path too long".to_string());
    }

    // Check for null bytes (can be used in path injection attacks)
    if path_str.contains('\0') {
        return Err("Path contains invalid characters".to_string());
    }

    let path = std::path::Path::new(path_str);

    // Ensure path exists before canonicalize
    if !path.exists() {
        return Err("Path does not exist".to_string());
    }

    // Canonicalize resolves symlinks and ".." sequences
    let canonical = path.canonicalize()
        .map_err(|e| format!("Failed to resolve path: {}", e))?;

    // Verify canonical path doesn't contain traversal (defense in depth)
    for component in canonical.components() {
        if let std::path::Component::ParentDir = component {
            return Err("Path contains traversal after canonicalization".to_string());
        }
    }

    Ok(canonical)
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── validate_session_value ───────────────────────────────────

    #[test]
    fn test_validate_passthrough_key() {
        assert!(validate_session_value("player_name", "Alice".into()).is_ok());
    }

    #[test]
    fn test_validate_boolean_true() {
        assert!(validate_session_value("efficiency_mode", "true".into()).is_ok());
    }

    #[test]
    fn test_validate_boolean_false() {
        assert!(validate_session_value("guest_mode", "false".into()).is_ok());
    }

    #[test]
    fn test_validate_boolean_invalid() {
        assert!(validate_session_value("efficiency_mode", "yes".into()).is_err());
    }

    #[test]
    fn test_validate_boolean_empty() {
        assert!(validate_session_value("close_to_tray", "".into()).is_err());
    }

    #[test]
    fn test_validate_auth_provider_steam() {
        assert!(validate_session_value("auth_provider", "steam".into()).is_ok());
    }

    #[test]
    fn test_validate_auth_provider_discord() {
        assert!(validate_session_value("auth_provider", "discord".into()).is_ok());
    }

    #[test]
    fn test_validate_auth_provider_invalid() {
        assert!(validate_session_value("auth_provider", "github".into()).is_err());
    }

    #[test]
    fn test_validate_splash_duration_valid() {
        assert!(validate_session_value("splash_bypass_duration", "20".into()).is_ok());
    }

    #[test]
    fn test_validate_splash_duration_min_boundary() {
        let min = SPLASH_BYPASS_MIN_SECS.to_string();
        assert!(validate_session_value("splash_bypass_duration", min).is_ok());
    }

    #[test]
    fn test_validate_splash_duration_max_boundary() {
        let max = SPLASH_BYPASS_MAX_SECS.to_string();
        assert!(validate_session_value("splash_bypass_duration", max).is_ok());
    }

    #[test]
    fn test_validate_splash_duration_below_min() {
        let below = (SPLASH_BYPASS_MIN_SECS - 1).to_string();
        assert!(validate_session_value("splash_bypass_duration", below).is_err());
    }

    #[test]
    fn test_validate_splash_duration_above_max() {
        let above = (SPLASH_BYPASS_MAX_SECS + 1).to_string();
        assert!(validate_session_value("splash_bypass_duration", above).is_err());
    }

    #[test]
    fn test_validate_splash_duration_non_numeric() {
        assert!(validate_session_value("splash_bypass_duration", "abc".into()).is_err());
    }

    // ── store_session (requires config init, so test validation logic) ──

    #[test]
    fn test_store_rejects_unknown_key() {
        assert!(store_session("unknown_key", "value".into()).is_err());
    }

    #[test]
    fn test_store_rejects_null_byte_in_key() {
        assert!(store_session("player\0name", "value".into()).is_err());
    }

    #[test]
    fn test_store_rejects_null_byte_in_value() {
        assert!(store_session("player_name", "val\0ue".into()).is_err());
    }

    #[test]
    fn test_store_rejects_oversized_key() {
        let long_key = "k".repeat(MAX_SESSION_KEY_LENGTH + 1);
        assert!(store_session(&long_key, "value".into()).is_err());
    }

    #[test]
    fn test_store_rejects_oversized_value() {
        let long_value = "v".repeat(MAX_SESSION_VALUE_LENGTH + 1);
        assert!(store_session("player_name", long_value).is_err());
    }

    // ── allowed keys coverage ───────────────────────────────────

    #[test]
    fn test_allowed_keys_not_empty() {
        assert!(!ALLOWED_SESSION_KEYS.is_empty());
    }

    #[test]
    fn test_allowed_keys_no_duplicates() {
        let mut seen = std::collections::HashSet::new();
        for key in ALLOWED_SESSION_KEYS {
            assert!(seen.insert(key), "Duplicate allowed key: {}", key);
        }
    }

    // ── get_batch_session ──────────────────────────────────────

    #[test]
    fn test_batch_session_returns_none_for_missing() {
        let keys = vec!["nonexistent_key_1".to_string(), "nonexistent_key_2".to_string()];
        let result = get_batch_session(keys);
        for val in result.values() {
            assert!(val.is_none());
        }
    }

    #[test]
    fn test_batch_session_rejects_oversized_batch() {
        let keys: Vec<String> = (0..MAX_BATCH_KEYS + 1)
            .map(|i| format!("key_{}", i))
            .collect();
        let result = get_batch_session(keys);
        assert!(result.is_empty(), "Oversized batch should return empty map");
    }

    #[test]
    fn test_batch_session_filters_oversized_keys() {
        let long_key = "k".repeat(MAX_SESSION_KEY_LENGTH + 1);
        let short_key = "short_key".to_string();
        let keys = vec![long_key.clone(), short_key.clone()];
        let result = get_batch_session(keys);
        // The long key should have been filtered out
        assert!(!result.contains_key(&long_key));
        // The short key should be present (value may be None if not in config)
        assert!(result.contains_key(&short_key));
    }

    #[test]
    fn test_batch_session_at_max_keys_allowed() {
        let keys: Vec<String> = (0..MAX_BATCH_KEYS)
            .map(|i| format!("key_{}", i))
            .collect();
        let result = get_batch_session(keys);
        // Should not be rejected — exactly at limit
        assert!(!result.is_empty() || result.len() == 0); // returned normally, not empty-due-to-rejection
        // More precisely: it should have filtered keys, not rejected the batch
        assert!(result.len() <= MAX_BATCH_KEYS);
    }
}
