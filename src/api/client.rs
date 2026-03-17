use std::sync::RwLock;
use std::time::Duration;

use once_cell::sync::Lazy;
use tracing::warn;
use zeroize::Zeroize;

use crate::api::types::*;

/// Valid OAuth provider names.
const VALID_PROVIDERS: &[&str] = &["steam", "discord"];

/// Validate that a provider name is in the allowed set.
fn validate_provider(provider: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    if VALID_PROVIDERS.contains(&provider) {
        Ok(())
    } else {
        Err(format!("Invalid provider: '{}'", provider).into())
    }
}

/// Validate that a Steam ID is numeric and within length bounds.
fn validate_steam_id(steam_id: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    if steam_id.is_empty() || steam_id.len() > 20 {
        return Err("Invalid Steam ID length".into());
    }
    if !steam_id.chars().all(|c| c.is_ascii_digit()) {
        return Err("Steam ID must be numeric".into());
    }
    Ok(())
}

/// Validate that a user ID is alphanumeric+hyphen and within length bounds.
fn validate_user_id(user_id: &str) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    if user_id.is_empty() || user_id.len() > 64 {
        return Err("Invalid user ID length".into());
    }
    if !user_id.chars().all(|c| c.is_ascii_alphanumeric() || c == '-') {
        return Err("User ID contains invalid characters".into());
    }
    Ok(())
}

/// API base URL
pub fn api_base_url() -> String {
    #[cfg(debug_assertions)]
    {
        std::env::var("ESPRIT_SEEDER_API_URL")
            .unwrap_or_else(|_| "https://seeding-api.espritdecorpsgaming.org".to_string())
    }
    #[cfg(not(debug_assertions))]
    {
        "https://seeding-api.espritdecorpsgaming.org".to_string()
    }
}

pub static HTTP_CLIENT: Lazy<reqwest::Client> = Lazy::new(|| {
    let mut default_headers = reqwest::header::HeaderMap::new();
    default_headers.insert(
        reqwest::header::HeaderName::from_static("x-client-version"),
        reqwest::header::HeaderValue::from_static(env!("CARGO_PKG_VERSION")),
    );

    reqwest::Client::builder()
        .timeout(Duration::from_secs(30))
        .user_agent(concat!("EspritSeeder/", env!("CARGO_PKG_VERSION")))
        .default_headers(default_headers)
        .build()
        .unwrap_or_else(|_| reqwest::Client::new())
});

// Auth tokens for the frontend API client (JWT + refresh)
static AUTH_TOKEN: Lazy<RwLock<Option<String>>> = Lazy::new(|| RwLock::new(None));
static REFRESH_TOKEN: Lazy<RwLock<Option<String>>> = Lazy::new(|| RwLock::new(None));

// API key for programmatic auth (alternative to JWT)
static API_KEY: Lazy<RwLock<Option<String>>> = Lazy::new(|| RwLock::new(None));

// Refresh deduplication — use a tokio Mutex to serialize refresh attempts
static REFRESH_LOCK: Lazy<tokio::sync::Mutex<()>> = Lazy::new(|| tokio::sync::Mutex::new(()));

/// Set auth tokens (called after OAuth callback or token refresh)
pub fn set_tokens(token: &str, refresh: &str) {
    {
        let mut t = write_lock!(AUTH_TOKEN);
        if let Some(ref mut old) = *t {
            old.zeroize();
        }
        *t = Some(token.to_string());
    }
    {
        let mut r = write_lock!(REFRESH_TOKEN);
        if let Some(ref mut old) = *r {
            old.zeroize();
        }
        *r = Some(refresh.to_string());
    }
    // Also pass token to the backend API client for monitor loop calls
    crate::backend::api_client::set_auth_token(token.to_string());
}

/// Clear auth state on logout
pub fn clear_tokens() {
    {
        let mut t = write_lock!(AUTH_TOKEN);
        if let Some(ref mut old) = *t {
            old.zeroize();
        }
        *t = None;
    }
    {
        let mut r = write_lock!(REFRESH_TOKEN);
        if let Some(ref mut old) = *r {
            old.zeroize();
        }
        *r = None;
    }
    crate::backend::api_client::set_auth_token(String::new());
}

/// Set the API key for programmatic auth
pub fn set_api_key(key: &str) {
    let mut k = write_lock!(API_KEY);
    if let Some(ref mut old) = *k {
        old.zeroize();
    }
    *k = Some(key.to_string());
    // Also set in backend API client
    crate::backend::api_client::set_api_key(key.to_string());
}

/// Clear the API key
pub fn clear_api_key() {
    let mut k = write_lock!(API_KEY);
    if let Some(ref mut old) = *k {
        old.zeroize();
    }
    *k = None;
    crate::backend::api_client::set_api_key(String::new());
}

/// Returns true if either JWT or API key is set
pub fn is_authenticated() -> bool {
    read_lock!(AUTH_TOKEN).is_some() || read_lock!(API_KEY).is_some()
}

pub(crate) fn get_token() -> Option<String> {
    read_lock!(AUTH_TOKEN).clone()
}

fn get_refresh_token() -> Option<String> {
    read_lock!(REFRESH_TOKEN).clone()
}

pub(crate) fn get_api_key_val() -> Option<String> {
    read_lock!(API_KEY).clone()
}

/// Attempt to refresh the JWT using the refresh token
async fn do_refresh() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let refresh = get_refresh_token().ok_or("No refresh token")?;

    let refresh_url = format!("{}/api/auth/refresh", api_base_url());
    let refresh_body = serde_json::json!({ "refresh_token": refresh });
    let response = crate::backend::retry::send_with_retry(|| {
        HTTP_CLIENT.post(&refresh_url).json(&refresh_body)
    })
    .await?;

    if !response.status().is_success() {
        clear_tokens();
        return Err("Token refresh failed".into());
    }

    let data: AuthRefreshResponse = response.json().await?;
    set_tokens(&data.token, &data.refresh_token);
    Ok(())
}

/// Refresh with deduplication — multiple concurrent 401s only trigger one refresh
pub(crate) async fn refresh_auth() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let _guard = REFRESH_LOCK.lock().await;
    do_refresh().await
}

/// Make an authenticated API request with auto-refresh on 401.
/// Uses JWT bearer token when available, falls back to x-api-key header.
async fn api_fetch<T: serde::de::DeserializeOwned>(
    method: reqwest::Method,
    path: &str,
    body: Option<serde_json::Value>,
) -> Result<T, Box<dyn std::error::Error + Send + Sync>> {
    let url = format!("{}{}", api_base_url(), path);

    let make_request = |token: &Option<String>, api_key: &Option<String>| {
        let mut req = HTTP_CLIENT.request(method.clone(), &url);
        if let Some(ref t) = token {
            req = req.bearer_auth(t);
        } else if let Some(ref k) = api_key {
            req = req.header("x-api-key", k.as_str());
        }
        if let Some(ref b) = body {
            req = req.json(b);
        }
        req
    };

    let token = get_token();
    let api_key = get_api_key_val();
    let response = crate::backend::retry::send_with_retry_notify(|| make_request(&token, &api_key)).await?;

    // Auto-refresh on 401 (only when using JWT)
    if response.status() == reqwest::StatusCode::UNAUTHORIZED && get_refresh_token().is_some() {
        if let Err(e) = refresh_auth().await {
            warn!("Token refresh failed: {}", e);
        } else {
            // Retry with new token
            let new_token = get_token();
            let response = crate::backend::retry::send_with_retry_notify(|| make_request(&new_token, &api_key)).await?;
            if !response.status().is_success() {
                let text = response.text().await.unwrap_or_default();
                return Err(format!("API error: {}", text).into());
            }
            return Ok(response.json().await?);
        }
    }

    if response.status().as_u16() == 426 {
        let text = response.text().await.unwrap_or_default();
        let min_version = serde_json::from_str::<serde_json::Value>(&text)
            .ok()
            .and_then(|v| v.get("minimum_version")?.as_str().map(String::from))
            .unwrap_or_else(|| "unknown".into());
        return Err(format!("Update required: minimum version is {min_version}. Please download the latest release.").into());
    }

    if !response.status().is_success() {
        let text = response.text().await.unwrap_or_default();
        return Err(format!("API error: {}", text).into());
    }

    Ok(response.json().await?)
}

/// Convert a raw API/network error into a short, user-friendly message.
pub fn friendly_error(err: &(dyn std::error::Error + Send + Sync)) -> String {
    let msg = err.to_string();

    // Network-level errors (from reqwest)
    if msg.contains("dns error") || msg.contains("No such host") {
        return "Could not reach server — check your internet connection".to_string();
    }
    if msg.contains("timed out") || msg.contains("Timeout") {
        return "Request timed out — the server may be slow, try again".to_string();
    }
    if msg.contains("connection refused") || msg.contains("Connection refused") {
        return "Server is not responding — try again later".to_string();
    }
    if msg.contains("connect error") || msg.contains("ConnectError") {
        return "Network error — check your internet connection".to_string();
    }

    // API-level errors
    if msg.contains("Update required") {
        return msg; // already user-friendly
    }
    if msg.contains("API error") {
        // Try to extract a JSON "error" field from the response body
        if let Some(start) = msg.find('{') {
            if let Ok(parsed) = serde_json::from_str::<serde_json::Value>(&msg[start..]) {
                if let Some(api_msg) = parsed.get("error").and_then(|v| v.as_str()) {
                    return api_msg.to_string();
                }
            }
        }
    }

    // Fallback — truncate long errors (char-aware to avoid splitting multi-byte UTF-8)
    if msg.len() > 80 {
        let truncated: String = msg.chars().take(77).collect();
        format!("{}...", truncated)
    } else {
        msg
    }
}

// ─── Public API methods ─────────────────────────────────────────────

/// Fetch server list (guest OK)
pub async fn get_servers() -> Result<ServersResponse, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(reqwest::Method::GET, "/api/servers", None).await
}

/// Fetch all server stats (guest OK)
pub async fn get_stats() -> Result<Vec<BatchStatsResult>, Box<dyn std::error::Error + Send + Sync>>
{
    api_fetch(reqwest::Method::GET, "/api/servers/stats", None).await
}

/// Get the next seeding candidate (auth required)
pub async fn get_next_server(
    game: &str,
    current_region: &str,
    current_index: usize,
    eu_enabled: bool,
    reason: &str,
    steam_id: Option<&str>,
) -> Result<NextServerResponse, Box<dyn std::error::Error + Send + Sync>> {
    let mut body = serde_json::json!({
        "game": game,
        "current_region": current_region,
        "current_index": current_index,
        "eu_enabled": eu_enabled,
        "reason": reason,
    });
    if let Some(sid) = steam_id {
        if let Some(obj) = body.as_object_mut() {
            obj.insert("steam_id".to_string(), serde_json::Value::String(sid.to_string()));
        }
    }
    api_fetch(
        reqwest::Method::POST,
        "/api/seeding/next-server",
        Some(body),
    )
    .await
}

/// Fetch pre-computed seeding status (best server per region)
pub async fn get_seeding_status() -> Result<SeedingStatusResponse, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(reqwest::Method::GET, "/api/seeding/status", None).await
}

/// Create a seeding session after successful game launch
pub async fn start_session(
    game: &str,
    region: &str,
    index: usize,
    steam_id: Option<&str>,
    analytics: Option<SessionStartAnalytics>,
) -> Result<StartSessionResponse, Box<dyn std::error::Error + Send + Sync>> {
    let mut body = serde_json::json!({
        "game": game,
        "region": region,
        "index": index,
    });
    if let Some(sid) = steam_id {
        if let Some(obj) = body.as_object_mut() {
            obj.insert("steam_id".to_string(), serde_json::Value::String(sid.to_string()));
        }
    }
    if let Some(a) = analytics {
        if let Some(obj) = body.as_object_mut() {
            obj.insert("os_version".to_string(), serde_json::json!(a.os_version));
            obj.insert("os_arch".to_string(), serde_json::json!(a.os_arch));
            obj.insert("efficiency_mode".to_string(), serde_json::json!(a.efficiency_mode));
            obj.insert("eu_enabled".to_string(), serde_json::json!(a.eu_enabled));
            obj.insert("auto_seed".to_string(), serde_json::json!(a.auto_seed));
        }
    }
    api_fetch(
        reqwest::Method::POST,
        "/api/seeding/start-session",
        Some(body),
    )
    .await
}

/// Analytics data collected at session start (non-PII).
#[derive(Debug, Clone)]
pub struct SessionStartAnalytics {
    pub os_version: String,
    pub os_arch: String,
    pub efficiency_mode: bool,
    pub eu_enabled: bool,
    pub auto_seed: bool,
}

/// Gather current analytics snapshot from app state.
pub fn gather_analytics(auto_seed: bool) -> SessionStartAnalytics {
    let efficiency_mode = crate::backend::session::get_stored_session("efficiency_mode")
        .map(|v| v == "true")
        .unwrap_or(false);
    let eu_enabled = crate::backend::session::get_stored_session("secondary_servers_enabled")
        .map(|v| v == "true")
        .unwrap_or(false);

    SessionStartAnalytics {
        os_version: crate::backend::os_info::os_version(),
        os_arch: crate::backend::os_info::os_arch().to_string(),
        efficiency_mode,
        eu_enabled,
        auto_seed,
    }
}

/// Register a guest account (unauthenticated — no api_fetch wrapper needed)
pub async fn register_guest() -> Result<crate::api::types::RegisterResponse, Box<dyn std::error::Error + Send + Sync>> {
    let url = format!("{}/api/auth/register", api_base_url());
    let response = crate::backend::retry::send_with_retry(|| {
        HTTP_CLIENT.post(&url).json(&serde_json::json!({}))
    })
    .await?;

    if !response.status().is_success() {
        let text = response.text().await.unwrap_or_default();
        return Err(format!("Registration failed: {}", text).into());
    }

    Ok(response.json().await?)
}

/// Get current user info (auth required)
pub async fn get_me() -> Result<UserInfo, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(reqwest::Method::GET, "/api/auth/me", None).await
}

/// Get linked steam IDs
pub async fn get_steam_ids() -> Result<Vec<SteamIdEntry>, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(reqwest::Method::GET, "/api/auth/steam-ids", None).await
}

/// Remove a linked steam ID
pub async fn remove_steam_id(steam_id: &str) -> Result<serde_json::Value, Box<dyn std::error::Error + Send + Sync>> {
    validate_steam_id(steam_id)?;
    api_fetch(
        reqwest::Method::DELETE,
        &format!("/api/auth/steam-ids/{}", steam_id),
        None,
    )
    .await
}

/// Send a seeding heartbeat
pub async fn send_heartbeat(session_id: &str) -> Result<HeartbeatResponse, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(
        reqwest::Method::POST,
        "/api/seeding/heartbeat",
        Some(serde_json::json!({ "session_id": session_id })),
    )
    .await
}

/// Stop a seeding session
pub async fn stop_session(
    session_id: &str,
    reason: Option<&str>,
) -> Result<StopSessionResponse, Box<dyn std::error::Error + Send + Sync>> {
    let mut body = serde_json::json!({ "session_id": session_id });
    if let Some(r) = reason {
        if let Some(obj) = body.as_object_mut() {
            obj.insert("reason".to_string(), serde_json::Value::String(r.to_string()));
        }
    }
    api_fetch(
        reqwest::Method::POST,
        "/api/seeding/stop",
        Some(body),
    )
    .await
}

/// Update the current user's display name (auth required)
pub async fn update_display_name(name: &str) -> Result<UserInfo, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(
        reqwest::Method::PATCH,
        "/api/auth/me",
        Some(serde_json::json!({ "display_name": name })),
    )
    .await
}

/// Generate a random anonymous nickname via the API (auth required)
pub async fn randomize_display_name() -> Result<UserInfo, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(
        reqwest::Method::PATCH,
        "/api/auth/me",
        Some(serde_json::json!({ "randomize": true })),
    )
    .await
}

/// Update leaderboard opt-out preference (auth required)
pub async fn update_leaderboard_opt_out(opt_out: bool) -> Result<UserInfo, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(
        reqwest::Method::PATCH,
        "/api/auth/me",
        Some(serde_json::json!({ "leaderboard_opt_out": opt_out })),
    )
    .await
}

// ─── Multi-provider auth ────────────────────────────────────────────

/// Get the OAuth URL for a given provider.
/// Returns an error string if the provider is invalid.
pub fn get_oauth_url(provider: &str, state: &str) -> Result<String, String> {
    if !VALID_PROVIDERS.contains(&provider) {
        return Err(format!("Invalid provider: '{}'", provider));
    }
    let encoded_state = urlencoding::encode(state);
    Ok(format!("{}/api/auth/{}?state={}", api_base_url(), provider, encoded_state))
}

/// Get the redirect URL to link an additional provider (after initial sign-in).
/// Calls POST /api/auth/{provider}/link-init which returns an HMAC-signed OAuth URL.
pub async fn get_link_redirect_url(provider: &str) -> Result<LinkInitResponse, Box<dyn std::error::Error + Send + Sync>> {
    validate_provider(provider)?;
    api_fetch(reqwest::Method::POST, &format!("/api/auth/{}/link-init", provider), None).await
}

/// Get all linked providers for the current user (auth required)
pub async fn get_linked_providers() -> Result<Vec<LinkedProvider>, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(reqwest::Method::GET, "/api/auth/providers", None).await
}

/// Rotate the API key for guest accounts. Returns the new key and updates local state.
pub async fn rotate_api_key() -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let resp: serde_json::Value = api_fetch(
        reqwest::Method::POST,
        "/api/auth/rotate-api-key",
        None,
    )
    .await?;
    let new_key = resp
        .get("api_key")
        .and_then(|v| v.as_str())
        .ok_or("Missing api_key in response")?
        .to_string();
    set_api_key(&new_key);
    Ok(new_key)
}

/// Delete the current user's account (auth required)
pub async fn delete_account() -> Result<serde_json::Value, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(reqwest::Method::DELETE, "/api/auth/account", None).await
}

/// Unlink a provider from the current account (auth required)
pub async fn unlink_provider(provider: &str) -> Result<serde_json::Value, Box<dyn std::error::Error + Send + Sync>> {
    validate_provider(provider)?;
    api_fetch(
        reqwest::Method::DELETE,
        &format!("/api/auth/providers/{}", provider),
        None,
    )
    .await
}

/// Fetch the seeding leaderboard (no auth required)
pub async fn get_leaderboard(
    days: i64,
    limit: i64,
) -> Result<Vec<LeaderboardEntry>, Box<dyn std::error::Error + Send + Sync>> {
    api_fetch(
        reqwest::Method::GET,
        &format!("/api/seeding/leaderboard?days={}&limit={}", days, limit),
        None,
    )
    .await
}

/// Fetch per-user seeding stats (auth required)
pub async fn get_user_stats(
    user_id: &str,
    days: i64,
) -> Result<UserSeedingStats, Box<dyn std::error::Error + Send + Sync>> {
    validate_user_id(user_id)?;
    api_fetch(
        reqwest::Method::GET,
        &format!("/api/seeding/stats/{}?days={}", user_id, days),
        None,
    )
    .await
}

/// Validate a display name client-side before sending to the API.
/// Returns Ok(()) if valid, or Err with a user-friendly message.
pub fn validate_display_name(name: &str) -> Result<(), String> {
    let trimmed = name.trim();

    if trimmed.len() < 2 {
        return Err("Name must be at least 2 characters".to_string());
    }
    if trimmed.len() > 32 {
        return Err("Name must be 32 characters or fewer".to_string());
    }

    // Character whitelist: letters, digits, spaces, hyphens, underscores, dots
    for ch in trimmed.chars() {
        if !ch.is_ascii_alphanumeric() && ch != ' ' && ch != '-' && ch != '_' && ch != '.' {
            return Err(format!(
                "Name contains invalid character '{}' — only letters, numbers, spaces, hyphens, underscores, and dots are allowed",
                ch
            ));
        }
    }

    // Profanity blocklist (matches server's name_validation.rs)
    const BLOCKED: &[&str] = &[
        "nigger", "nigga", "faggot", "retard", "chink", "spic", "kike",
        "tranny", "coon", "gook", "wetback", "beaner", "raghead",
    ];
    let lower = trimmed.to_lowercase();
    for word in BLOCKED {
        if lower.contains(word) {
            return Err("Name contains inappropriate language".to_string());
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Simple error type for testing friendly_error()
    #[derive(Debug)]
    struct TestError(String);

    impl std::fmt::Display for TestError {
        fn fmt(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
            write!(f, "{}", self.0)
        }
    }

    impl std::error::Error for TestError {}

    fn fe(msg: &str) -> String {
        let err = TestError(msg.to_string());
        friendly_error(&err)
    }

    #[test]
    fn test_friendly_error_dns() {
        let result = fe("dns error: failed to lookup address");
        assert_eq!(result, "Could not reach server — check your internet connection");
    }

    #[test]
    fn test_friendly_error_no_such_host() {
        let result = fe("No such host is known");
        assert_eq!(result, "Could not reach server — check your internet connection");
    }

    #[test]
    fn test_friendly_error_timeout() {
        let result = fe("operation timed out");
        assert_eq!(result, "Request timed out — the server may be slow, try again");
    }

    #[test]
    fn test_friendly_error_timeout_capital() {
        let result = fe("Timeout waiting for response");
        assert_eq!(result, "Request timed out — the server may be slow, try again");
    }

    #[test]
    fn test_friendly_error_connection_refused() {
        let result = fe("Connection refused (os error 111)");
        assert_eq!(result, "Server is not responding — try again later");
    }

    #[test]
    fn test_friendly_error_connect_error() {
        let result = fe("connect error: Connection reset");
        assert_eq!(result, "Network error — check your internet connection");
    }

    #[test]
    fn test_friendly_error_update_required() {
        let msg = "Update required: minimum version is 2.0.0. Please download the latest release.";
        let result = fe(msg);
        assert_eq!(result, msg);
    }

    #[test]
    fn test_friendly_error_api_json_error() {
        let result = fe(r#"API error: {"error": "Invalid credentials", "code": 401}"#);
        assert_eq!(result, "Invalid credentials");
    }

    #[test]
    fn test_friendly_error_api_non_json() {
        let result = fe("API error: Internal Server Error");
        assert_eq!(result, "API error: Internal Server Error");
    }

    #[test]
    fn test_friendly_error_truncate_long_message() {
        let long = "A".repeat(100);
        let result = fe(&long);
        assert_eq!(result.len(), 80); // 77 chars + "..."
        assert!(result.ends_with("..."));
    }

    #[test]
    fn test_friendly_error_short_passthrough() {
        let result = fe("Something went wrong");
        assert_eq!(result, "Something went wrong");
    }

    // ─── validate_display_name tests ────────────────────────────────

    #[test]
    fn test_validate_name_valid() {
        assert!(validate_display_name("Alice").is_ok());
        assert!(validate_display_name("Bob 123").is_ok());
        assert!(validate_display_name("my-name_here.ok").is_ok());
        assert!(validate_display_name("ab").is_ok()); // minimum length
        assert!(validate_display_name(&"a".repeat(32)).is_ok()); // max length
    }

    #[test]
    fn test_validate_name_too_short() {
        assert_eq!(
            validate_display_name("a"),
            Err("Name must be at least 2 characters".to_string())
        );
        assert_eq!(
            validate_display_name(""),
            Err("Name must be at least 2 characters".to_string())
        );
        assert_eq!(
            validate_display_name("   "),
            Err("Name must be at least 2 characters".to_string())
        );
    }

    #[test]
    fn test_validate_name_too_long() {
        assert_eq!(
            validate_display_name(&"a".repeat(33)),
            Err("Name must be 32 characters or fewer".to_string())
        );
    }

    #[test]
    fn test_validate_name_invalid_chars() {
        let result = validate_display_name("hello@world");
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("'@'"));

        assert!(validate_display_name("name#tag").is_err());
        assert!(validate_display_name("emoji😀").is_err());
    }

    #[test]
    fn test_validate_name_profanity() {
        assert_eq!(
            validate_display_name("xNiggerx"),
            Err("Name contains inappropriate language".to_string())
        );
        assert_eq!(
            validate_display_name("FAGGOT"),
            Err("Name contains inappropriate language".to_string())
        );
    }

    #[test]
    fn test_validate_name_trims_whitespace() {
        assert!(validate_display_name("  Alice  ").is_ok());
    }

    #[test]
    fn test_get_oauth_url_steam() {
        let url = get_oauth_url("steam", "state123").unwrap();
        assert!(url.contains("/api/auth/steam"), "URL should contain provider path");
        assert!(url.ends_with("?state=state123"));
    }

    #[test]
    fn test_get_oauth_url_discord() {
        let url = get_oauth_url("discord", "xyz").unwrap();
        assert!(url.contains("/api/auth/discord"));
        assert!(url.contains("state=xyz"));
    }

    #[test]
    fn test_get_oauth_url_invalid_provider() {
        assert!(get_oauth_url("twitch", "state").is_err());
        assert!(get_oauth_url("", "state").is_err());
        assert!(get_oauth_url("../evil", "state").is_err());
    }

    #[test]
    fn test_get_oauth_url_encodes_state() {
        let url = get_oauth_url("steam", "state with spaces&special=chars").unwrap();
        assert!(!url.contains(' '));
        assert!(url.contains("state%20with%20spaces%26special%3Dchars"));
    }

    #[test]
    fn test_api_base_url_format() {
        let base = api_base_url();
        assert!(base.starts_with("https://"), "base URL should use HTTPS");
        assert!(!base.ends_with('/'), "base URL should not have trailing slash");
    }

    #[test]
    fn test_get_oauth_url_format() {
        let url = get_oauth_url("steam", "state").unwrap();
        let base = api_base_url();
        assert_eq!(url, format!("{}/api/auth/steam?state=state", base));
    }

    #[test]
    fn test_get_oauth_url_preserves_state() {
        let url = get_oauth_url("steam", "abc-123_xyz").unwrap();
        assert!(url.contains("state=abc-123_xyz"));
    }

    // ─── validate_steam_id tests ─────────────────────────────────────

    #[test]
    fn test_validate_steam_id_valid() {
        assert!(validate_steam_id("76561198000000000").is_ok());
        assert!(validate_steam_id("1").is_ok());
        assert!(validate_steam_id("12345678901234567890").is_ok()); // 20 digits
    }

    #[test]
    fn test_validate_steam_id_invalid() {
        assert!(validate_steam_id("").is_err());
        assert!(validate_steam_id("123456789012345678901").is_err()); // 21 digits
        assert!(validate_steam_id("abc123").is_err());
        assert!(validate_steam_id("1234-5678").is_err());
        assert!(validate_steam_id("../etc/passwd").is_err());
    }

    // ─── validate_user_id tests ──────────────────────────────────────

    #[test]
    fn test_validate_user_id_valid() {
        assert!(validate_user_id("abc123").is_ok());
        assert!(validate_user_id("550e8400-e29b-41d4-a716-446655440000").is_ok()); // UUID
        assert!(validate_user_id("a").is_ok());
    }

    #[test]
    fn test_validate_user_id_invalid() {
        assert!(validate_user_id("").is_err());
        assert!(validate_user_id(&"a".repeat(65)).is_err());
        assert!(validate_user_id("user/path").is_err());
        assert!(validate_user_id("user;drop").is_err());
        assert!(validate_user_id("user name").is_err());
    }

    // ─── validate_provider tests ─────────────────────────────────────

    #[test]
    fn test_validate_provider_valid() {
        assert!(validate_provider("steam").is_ok());
        assert!(validate_provider("discord").is_ok());
    }

    #[test]
    fn test_validate_provider_invalid() {
        assert!(validate_provider("twitch").is_err());
        assert!(validate_provider("").is_err());
        assert!(validate_provider("../evil").is_err());
    }

    // ─── friendly_error UTF-8 test ───────────────────────────────────

    #[test]
    fn test_friendly_error_multibyte_utf8() {
        // 80 multi-byte chars should trigger truncation without panic
        let multibyte = "\u{00E9}".repeat(100); // 'é' is 2 bytes each
        let result = fe(&multibyte);
        assert!(result.ends_with("..."));
        // Should have 77 chars + "..."
        let char_count = result.chars().count();
        assert_eq!(char_count, 80); // 77 + 3 dots
    }
}
