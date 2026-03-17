use tracing::{info, warn};
use winreg::enums::*;
use winreg::RegKey;

/// Valid provider names for link callbacks.
const VALID_PROVIDERS: &[&str] = &["steam", "discord"];

/// Maximum length for any single deep link parameter key or value.
const MAX_PARAM_LEN: usize = 4096;

/// Maximum number of query parameters accepted in a deep link.
const MAX_PARAMS: usize = 16;

/// Parsed deep link actions
#[derive(Debug, Clone)]
pub enum DeepLinkAction {
    /// OAuth callback with JWT tokens (login flow)
    AuthCallback {
        state: Option<String>,
        token: String,
        refresh_token: String,
    },
    /// Provider link callback (linking an additional provider to existing account)
    LinkCallback {
        provider: String,
        provider_id: String,
    },
    /// Unknown or unsupported deep link
    Unknown(String),
}

/// Parse a `espritseeder://` URL into a DeepLinkAction.
/// Expected format: `espritseeder://auth/callback?token=...&refresh_token=...`
pub fn parse_deep_link(url: &str) -> Option<DeepLinkAction> {
    let url = url.trim();
    if !url.starts_with("espritseeder://") {
        return None;
    }

    // Parse using url crate-like manual parsing
    let after_scheme = &url["espritseeder://".len()..];

    // Split path and query
    let (path, query) = if let Some(idx) = after_scheme.find('?') {
        (&after_scheme[..idx], &after_scheme[idx + 1..])
    } else {
        (after_scheme, "")
    };

    let params: std::collections::HashMap<String, String> = query
        .split('&')
        .filter_map(|pair| {
            let mut parts = pair.splitn(2, '=');
            let key = urlencoding::decode(parts.next()?).ok()?.into_owned();
            let val = parts
                .next()
                .and_then(|v| urlencoding::decode(v).ok())
                .map(|v| v.into_owned())
                .unwrap_or_default();
            if key.len() > MAX_PARAM_LEN || val.len() > MAX_PARAM_LEN {
                return None;
            }
            Some((key, val))
        })
        .take(MAX_PARAMS)
        .collect();

    match path {
        "auth/callback" => {
            let state = params.get("state").cloned();
            let token = params.get("token").cloned();
            let refresh_token = params.get("refresh_token").cloned();

            if let (Some(token), Some(refresh_token)) = (token, refresh_token) {
                if !token.is_empty() && !refresh_token.is_empty() {
                    return Some(DeepLinkAction::AuthCallback {
                        state,
                        token,
                        refresh_token,
                    });
                }
            }
            warn!("Auth callback deep link missing token or refresh_token");
            Some(DeepLinkAction::Unknown(url.to_string()))
        }
        "auth/link-callback" => {
            let provider = params.get("provider").cloned();
            let provider_id = params.get("provider_id").cloned();

            if let (Some(provider), Some(provider_id)) = (provider, provider_id) {
                if !VALID_PROVIDERS.contains(&provider.as_str()) {
                    warn!("Link callback with unknown provider: {}", provider);
                    return Some(DeepLinkAction::Unknown(url.to_string()));
                }
                if !provider.is_empty() && !provider_id.is_empty() {
                    return Some(DeepLinkAction::LinkCallback {
                        provider,
                        provider_id,
                    });
                }
            }
            warn!("Link callback deep link missing provider or provider_id");
            Some(DeepLinkAction::Unknown(url.to_string()))
        }
        _ => Some(DeepLinkAction::Unknown(url.to_string())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_valid_auth_callback() {
        let url = "espritseeder://auth/callback?token=abc123&refresh_token=def456";
        let action = parse_deep_link(url).unwrap();
        match action {
            DeepLinkAction::AuthCallback { state, token, refresh_token } => {
                assert_eq!(token, "abc123");
                assert_eq!(refresh_token, "def456");
                assert!(state.is_none());
            }
            _ => panic!("Expected AuthCallback, got {:?}", action),
        }
    }

    #[test]
    fn test_parse_auth_callback_with_state() {
        let url = "espritseeder://auth/callback?token=abc&refresh_token=def&state=xyz789";
        let action = parse_deep_link(url).unwrap();
        match action {
            DeepLinkAction::AuthCallback { state, token, refresh_token } => {
                assert_eq!(token, "abc");
                assert_eq!(refresh_token, "def");
                assert_eq!(state.unwrap(), "xyz789");
            }
            _ => panic!("Expected AuthCallback"),
        }
    }

    #[test]
    fn test_parse_auth_callback_missing_refresh_token() {
        let url = "espritseeder://auth/callback?token=abc123";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::Unknown(_)));
    }

    #[test]
    fn test_parse_auth_callback_empty_token() {
        let url = "espritseeder://auth/callback?token=&refresh_token=def";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::Unknown(_)));
    }

    #[test]
    fn test_parse_auth_callback_empty_refresh_token() {
        let url = "espritseeder://auth/callback?token=abc&refresh_token=";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::Unknown(_)));
    }

    #[test]
    fn test_parse_valid_link_callback_steam() {
        let url = "espritseeder://auth/link-callback?provider=steam&provider_id=12345";
        let action = parse_deep_link(url).unwrap();
        match action {
            DeepLinkAction::LinkCallback { provider, provider_id } => {
                assert_eq!(provider, "steam");
                assert_eq!(provider_id, "12345");
            }
            _ => panic!("Expected LinkCallback"),
        }
    }

    #[test]
    fn test_parse_valid_link_callback_discord() {
        let url = "espritseeder://auth/link-callback?provider=discord&provider_id=99999";
        let action = parse_deep_link(url).unwrap();
        match action {
            DeepLinkAction::LinkCallback { provider, provider_id } => {
                assert_eq!(provider, "discord");
                assert_eq!(provider_id, "99999");
            }
            _ => panic!("Expected LinkCallback"),
        }
    }

    #[test]
    fn test_parse_link_callback_invalid_provider() {
        let url = "espritseeder://auth/link-callback?provider=twitch&provider_id=123";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::Unknown(_)));
    }

    #[test]
    fn test_parse_link_callback_missing_provider_id() {
        let url = "espritseeder://auth/link-callback?provider=steam";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::Unknown(_)));
    }

    #[test]
    fn test_parse_url_encoded_params() {
        let url = "espritseeder://auth/callback?token=abc%20def&refresh_token=ghi%26jkl";
        let action = parse_deep_link(url).unwrap();
        match action {
            DeepLinkAction::AuthCallback { token, refresh_token, .. } => {
                assert_eq!(token, "abc def");
                assert_eq!(refresh_token, "ghi&jkl");
            }
            _ => panic!("Expected AuthCallback"),
        }
    }

    #[test]
    fn test_parse_not_hllseeder_scheme() {
        assert!(parse_deep_link("https://example.com").is_none());
        assert!(parse_deep_link("").is_none());
        assert!(parse_deep_link("hll://auth/callback").is_none());
    }

    #[test]
    fn test_parse_unknown_path() {
        let url = "espritseeder://some/random/path";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::Unknown(_)));
    }

    #[test]
    fn test_parse_whitespace_trimmed() {
        let url = "  espritseeder://auth/callback?token=abc&refresh_token=def  ";
        let action = parse_deep_link(url).unwrap();
        assert!(matches!(action, DeepLinkAction::AuthCallback { .. }));
    }

    #[test]
    fn test_valid_providers_whitelist() {
        assert!(VALID_PROVIDERS.contains(&"steam"));
        assert!(VALID_PROVIDERS.contains(&"discord"));
        assert!(!VALID_PROVIDERS.contains(&"twitch"));
        assert!(!VALID_PROVIDERS.contains(&"google"));
        assert!(!VALID_PROVIDERS.contains(&""));
    }
}

/// Register the `espritseeder://` URL protocol in the Windows Registry.
/// This allows Discord OAuth callbacks to open the app.
pub fn register_deep_link_protocol() {
    let exe_path = match std::env::current_exe() {
        Ok(p) => p.to_string_lossy().to_string(),
        Err(e) => {
            warn!("Failed to get current exe path for deep link registration: {}", e);
            return;
        }
    };

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let classes_path = r"Software\Classes\espritseeder";

    match hkcu.create_subkey(classes_path) {
        Ok((key, _)) => {
            let _ = key.set_value("", &"URL:Esprit Seeder Protocol");
            let _ = key.set_value("URL Protocol", &"");

            match key.create_subkey(r"shell\open\command") {
                Ok((cmd_key, _)) => {
                    let command = format!("\"{}\" \"%1\"", exe_path);
                    let _ = cmd_key.set_value("", &command);
                    info!("Deep link protocol registered: espritseeder://");
                }
                Err(e) => {
                    warn!("Failed to create command subkey: {}", e);
                }
            }
        }
        Err(e) => {
            warn!("Failed to register deep link protocol: {}", e);
        }
    }
}
