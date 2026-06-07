use std::sync::RwLock;

use dioxus::prelude::*;
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};

/// User info — supports all auth providers
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct UserInfo {
    pub user_id: String,
    pub username: String,
    #[serde(default)]
    pub auth_provider: AuthProvider,
    #[serde(default)]
    pub display_name: Option<String>,
    #[serde(default)]
    pub steam_id: Option<String>,
    #[serde(default)]
    pub discord_id: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub created_at: u64,
    #[serde(default)]
    pub last_seen_at: u64,
    #[serde(default)]
    pub leaderboard_opt_out: bool,
}

/// Auth provider used to sign in
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum AuthProvider {
    #[default]
    Steam,
    Discord,
    Guest,
}

/// How the user is authenticated
#[derive(Debug, Clone, Default, PartialEq)]
pub enum AuthMethod {
    #[default]
    None,
    ApiKey,
    Jwt,
}

// Auth state
pub static USER: GlobalSignal<Option<UserInfo>> = Signal::global(|| None);
pub static IS_LOGGED_IN: GlobalSignal<bool> = Signal::global(|| false);
pub static AUTH_LOADING: GlobalSignal<bool> = Signal::global(|| false);
pub static AUTH_METHOD: GlobalSignal<AuthMethod> = Signal::global(AuthMethod::default);
pub static AUTH_PROVIDER: GlobalSignal<Option<AuthProvider>> = Signal::global(|| None);
pub static IS_GUEST: GlobalSignal<bool> = Signal::global(|| false);
pub static ONBOARDING_COMPLETE: GlobalSignal<bool> = Signal::global(|| false);
pub static ONBOARDING_STEP: GlobalSignal<usize> = Signal::global(|| 0);
/// OAuth state parameter for CSRF protection.
/// Stored when initiating OAuth and validated on callback.
static OAUTH_STATE: Lazy<RwLock<Option<String>>> = Lazy::new(|| RwLock::new(None));

/// Store the OAuth state before redirecting to the provider.
pub fn set_oauth_state(state: String) {
    let mut s = write_lock!(OAUTH_STATE);
    *s = Some(state);
}

/// Validate and consume the stored OAuth state.
/// Returns true if the provided state matches the stored one (single-use).
pub fn validate_oauth_state(state: &str) -> bool {
    let mut s = write_lock!(OAUTH_STATE);
    match s.take() {
        Some(stored) => stored == state,
        None => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_auth_method_default_is_none() {
        assert_eq!(AuthMethod::default(), AuthMethod::None);
    }

    #[test]
    fn test_auth_method_equality() {
        assert_eq!(AuthMethod::None, AuthMethod::None);
        assert_eq!(AuthMethod::ApiKey, AuthMethod::ApiKey);
        assert_eq!(AuthMethod::Jwt, AuthMethod::Jwt);
        assert_ne!(AuthMethod::None, AuthMethod::ApiKey);
        assert_ne!(AuthMethod::None, AuthMethod::Jwt);
        assert_ne!(AuthMethod::ApiKey, AuthMethod::Jwt);
    }

    #[test]
    fn test_auth_method_debug() {
        assert_eq!(format!("{:?}", AuthMethod::None), "None");
        assert_eq!(format!("{:?}", AuthMethod::ApiKey), "ApiKey");
        assert_eq!(format!("{:?}", AuthMethod::Jwt), "Jwt");
    }

    #[test]
    fn test_auth_method_clone() {
        let method = AuthMethod::Jwt;
        let cloned = method.clone();
        assert_eq!(method, cloned);
    }

    #[test]
    fn test_auth_provider_equality() {
        assert_eq!(AuthProvider::Steam, AuthProvider::Steam);
        assert_eq!(AuthProvider::Discord, AuthProvider::Discord);
        assert_eq!(AuthProvider::Guest, AuthProvider::Guest);
        assert_ne!(AuthProvider::Steam, AuthProvider::Discord);
        assert_ne!(AuthProvider::Steam, AuthProvider::Guest);
    }

    #[test]
    fn test_auth_provider_default_is_steam() {
        assert_eq!(AuthProvider::default(), AuthProvider::Steam);
    }

    #[test]
    fn test_auth_provider_serde() {
        for provider in [AuthProvider::Steam, AuthProvider::Discord, AuthProvider::Guest] {
            let json = serde_json::to_string(&provider).unwrap();
            let deserialized: AuthProvider = serde_json::from_str(&json).unwrap();
            assert_eq!(deserialized, provider);
        }
    }

    #[test]
    fn test_user_info_default() {
        let user = UserInfo::default();
        assert_eq!(user.user_id, "");
        assert_eq!(user.username, "");
        assert_eq!(user.auth_provider, AuthProvider::Steam);
        assert!(user.display_name.is_none());
        assert!(user.steam_id.is_none());
        assert!(user.discord_id.is_none());
        assert!(user.avatar.is_none());
        assert_eq!(user.created_at, 0);
        assert_eq!(user.last_seen_at, 0);
    }

    #[test]
    fn test_user_info_serde_roundtrip() {
        let user = UserInfo {
            user_id: "1".to_string(),
            username: "test".to_string(),
            auth_provider: AuthProvider::Discord,
            display_name: Some("Test User".to_string()),
            steam_id: None,
            discord_id: Some("123".to_string()),
            avatar: None,
            created_at: 1000,
            last_seen_at: 2000,
            leaderboard_opt_out: false,
        };
        let json = serde_json::to_string(&user).unwrap();
        let deserialized: UserInfo = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized, user);
    }

    #[test]
    fn test_user_info_deserialize_minimal() {
        let json = r#"{"user_id":"42","username":"min"}"#;
        let user: UserInfo = serde_json::from_str(json).unwrap();
        assert_eq!(user.user_id, "42");
        assert_eq!(user.auth_provider, AuthProvider::Steam); // default
    }

    // These tests share the OAUTH_STATE static, so we run all assertions
    // in a single test to avoid parallel interference.
    #[test]
    fn test_oauth_state_lifecycle() {
        // 1. Validate with no state set → false
        assert!(!validate_oauth_state("anything"));

        // 2. Set state, validate with correct value → true
        set_oauth_state("state_abc".to_string());
        assert!(validate_oauth_state("state_abc"));

        // 3. State is consumed — second validate → false
        assert!(!validate_oauth_state("state_abc"));

        // 4. Set state, validate with wrong value → false (and consumes)
        set_oauth_state("correct_state".to_string());
        assert!(!validate_oauth_state("wrong_state"));
        // State was consumed by the failed validation
        assert!(!validate_oauth_state("correct_state"));

        // 5. Set state, overwrite, validate → only latest works
        set_oauth_state("first".to_string());
        set_oauth_state("second".to_string());
        assert!(!validate_oauth_state("first"));
        // "second" was consumed by the failed "first" check above
        // Let's verify a clean overwrite scenario
        set_oauth_state("overwritten".to_string());
        assert!(validate_oauth_state("overwritten"));

        // 6. Empty string state
        set_oauth_state("".to_string());
        assert!(validate_oauth_state(""));
        assert!(!validate_oauth_state(""));

        // 7. Long state (1000 chars)
        let long_state = "x".repeat(1000);
        set_oauth_state(long_state.clone());
        assert!(validate_oauth_state(&long_state));
        assert!(!validate_oauth_state(&long_state));

        // 8. Special characters in state
        let special = "state+with/special=chars&more?query#frag";
        set_oauth_state(special.to_string());
        assert!(validate_oauth_state(special));
    }
}
