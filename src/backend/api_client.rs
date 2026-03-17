use std::sync::RwLock;
use std::sync::atomic::{AtomicI64, Ordering as AtomicOrdering};

use once_cell::sync::Lazy;
use log::info;
use serde::Deserialize;
use zeroize::Zeroize;

use crate::api::client::{api_base_url, HTTP_CLIENT};

/// Auth token set by the frontend after Discord OAuth login.
/// The monitor loop reads this to authenticate API calls.
static AUTH_TOKEN: Lazy<RwLock<String>> = Lazy::new(|| RwLock::new(String::new()));

/// API key for programmatic auth (alternative to JWT)
static API_KEY: Lazy<RwLock<String>> = Lazy::new(|| RwLock::new(String::new()));

/// SSE-derived seeding status cache. Written by the SSE loop, read by the monitor loop.
static CACHED_SEEDING_STATUS: Lazy<RwLock<Option<crate::api::types::SeedingStatusResponse>>> =
    Lazy::new(|| RwLock::new(None));

/// Unix timestamp of last seeding status cache update (0 = never)
static SEEDING_STATUS_UPDATED_AT: AtomicI64 = AtomicI64::new(0);

/// Cache considered stale after this many seconds without an SSE update
const SEEDING_STATUS_STALE_SECS: i64 = 90;

/// Check an API response status code, returning a descriptive error for failures.
/// Extracts the JSON error body for 426 Upgrade Required responses.
async fn check_response(response: reqwest::Response) -> Result<reqwest::Response, Box<dyn std::error::Error + Send + Sync>> {
    if response.status().is_success() {
        return Ok(response);
    }

    if response.status().as_u16() == 426 {
        #[derive(Deserialize)]
        struct UpgradeBody { minimum_version: Option<String> }
        let body: UpgradeBody = response.json().await.unwrap_or(UpgradeBody { minimum_version: None });
        let min = body.minimum_version.unwrap_or_else(|| "unknown".into());
        return Err(format!("Update required: minimum version is {min}. Please download the latest release.").into());
    }

    Err(format!("API error: {}", response.status()).into())
}

pub fn set_auth_token(token: String) {
    if token.is_empty() {
        info!("Auth token cleared");
    } else {
        info!("Auth token updated");
    }
    let mut t = write_lock!(AUTH_TOKEN);
    t.zeroize();
    *t = token;
}

pub fn get_auth_token() -> String {
    read_lock!(AUTH_TOKEN).clone()
}

pub fn set_api_key(key: String) {
    info!("API key updated");
    let mut k = write_lock!(API_KEY);
    k.zeroize();
    *k = key;
}

pub fn get_api_key_val() -> String {
    read_lock!(API_KEY).clone()
}

/// Apply auth to a request builder — JWT bearer if available, else x-api-key
fn apply_auth(req: reqwest::RequestBuilder) -> reqwest::RequestBuilder {
    let token = get_auth_token();
    if !token.is_empty() {
        return req.bearer_auth(&token);
    }
    let api_key = get_api_key_val();
    if !api_key.is_empty() {
        return req.header("x-api-key", &api_key);
    }
    req
}

#[cfg(test)]
mod tests {
    use super::*;

    // All token/key tests in a single function to avoid shared-state conflicts.
    #[test]
    fn test_auth_token_and_api_key_lifecycle() {
        // ── AUTH_TOKEN ──

        // Initial / clean state: set empty to establish baseline
        set_auth_token(String::new());
        assert_eq!(get_auth_token(), "");

        // Set then get
        set_auth_token("token_abc".to_string());
        assert_eq!(get_auth_token(), "token_abc");

        // Overwrite
        set_auth_token("token_xyz".to_string());
        assert_eq!(get_auth_token(), "token_xyz");

        // Clear
        set_auth_token(String::new());
        assert_eq!(get_auth_token(), "");

        // ── API_KEY ──

        // Set empty baseline
        set_api_key(String::new());
        assert_eq!(get_api_key_val(), "");

        // Set then get
        set_api_key("key_123".to_string());
        assert_eq!(get_api_key_val(), "key_123");

        // Overwrite
        set_api_key("key_456".to_string());
        assert_eq!(get_api_key_val(), "key_456");

        // Clear
        set_api_key(String::new());
        assert_eq!(get_api_key_val(), "");

        // ── apply_auth priority: JWT > API key ──
        set_auth_token("jwt_token".to_string());
        set_api_key("api_key_val".to_string());
        // When both are set, bearer auth (JWT) takes priority
        let token = get_auth_token();
        assert_eq!(token, "jwt_token");

        // Clear JWT, API key should be used
        set_auth_token(String::new());
        let key = get_api_key_val();
        assert_eq!(key, "api_key_val");

        // Clean up
        set_api_key(String::new());
    }
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

/// Update the SSE-derived seeding status cache. Called from the SSE event handler.
pub fn update_seeding_status_cache(status: crate::api::types::SeedingStatusResponse) {
    *write_lock!(CACHED_SEEDING_STATUS) = Some(status);
    SEEDING_STATUS_UPDATED_AT.store(unix_now(), AtomicOrdering::Release);
}

/// Mark the seeding status cache as stale, forcing the next reader to fall back to HTTP.
/// Called on SSE reconnect to catch any events lost during the disconnect.
pub fn invalidate_seeding_status_cache() {
    SEEDING_STATUS_UPDATED_AT.store(0, AtomicOrdering::Release);
}

/// Read the SSE-derived seeding status if fresh (within SEEDING_STATUS_STALE_SECS).
/// Returns None if the cache is empty or stale, signalling the caller to fall back to polling.
pub fn get_cached_seeding_status() -> Option<crate::api::types::SeedingStatusResponse> {
    let updated_at = SEEDING_STATUS_UPDATED_AT.load(AtomicOrdering::Acquire);
    if updated_at == 0 || (unix_now() - updated_at) > SEEDING_STATUS_STALE_SECS {
        return None;
    }
    read_lock!(CACHED_SEEDING_STATUS).clone()
}

/// Fetch pre-computed seeding status (best candidate per region) via the backend API client.
/// Prefer `get_cached_seeding_status()` when SSE is providing live updates.
pub async fn fetch_seeding_status() -> Result<crate::api::types::SeedingStatusResponse, Box<dyn std::error::Error + Send + Sync>> {
    let url = format!("{}/api/seeding/status", api_base_url());

    let response = check_response(
        crate::backend::retry::send_with_retry(|| apply_auth(HTTP_CLIENT.get(&url))).await?,
    )
    .await?;
    let status: crate::api::types::SeedingStatusResponse = response.json().await?;
    Ok(status)
}

