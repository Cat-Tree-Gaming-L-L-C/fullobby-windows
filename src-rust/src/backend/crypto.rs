use base64::{Engine as _, engine::general_purpose::STANDARD as BASE64};
use tracing::error;
use winapi::um::dpapi::{CryptProtectData, CryptUnprotectData};
use winapi::um::winbase::LocalFree;
use winapi::um::wincrypt::CRYPTOAPI_BLOB;

/// Keys whose values are encrypted with DPAPI before writing to config.
pub const SENSITIVE_KEYS: &[&str] = &["auth_token", "auth_refresh_token", "api_key"];

/// Prefix prepended to encrypted values to distinguish them from plaintext.
const ENCRYPTED_PREFIX: &str = "dpapi:";

/// Encrypt a plaintext string using Windows DPAPI (current-user scope).
/// Returns a base64-encoded ciphertext prefixed with `dpapi:`.
pub fn encrypt(plaintext: &str) -> Result<String, String> {
    let input = plaintext.as_bytes();

    let mut data_in = CRYPTOAPI_BLOB {
        cbData: input.len() as u32,
        pbData: input.as_ptr() as *mut u8,
    };

    let mut data_out = CRYPTOAPI_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };

    let ok = unsafe {
        CryptProtectData(
            &mut data_in,
            std::ptr::null(),     // description (unused)
            std::ptr::null_mut(), // optional entropy
            std::ptr::null_mut(), // reserved
            std::ptr::null_mut(), // prompt struct
            0,                    // flags
            &mut data_out,
        )
    };

    if ok == 0 {
        return Err("DPAPI CryptProtectData failed".to_string());
    }

    let encrypted =
        unsafe { std::slice::from_raw_parts(data_out.pbData, data_out.cbData as usize) };
    let encoded = format!("{}{}", ENCRYPTED_PREFIX, BASE64.encode(encrypted));

    unsafe {
        // Zero the DPAPI output buffer before freeing — it may contain
        // intermediate ciphertext data allocated by Windows.
        std::ptr::write_bytes(data_out.pbData, 0, data_out.cbData as usize);
        LocalFree(data_out.pbData as *mut _);
    }

    Ok(encoded)
}

/// Decrypt a DPAPI-encrypted value. If the value doesn't have the `dpapi:`
/// prefix it is returned as-is (plaintext migration path).
pub fn decrypt(value: &str) -> Result<String, String> {
    let ciphertext_b64 = match value.strip_prefix(ENCRYPTED_PREFIX) {
        Some(b64) => b64,
        None => return Ok(value.to_string()), // plaintext passthrough
    };

    let ciphertext = BASE64
        .decode(ciphertext_b64)
        .map_err(|e| format!("Base64 decode failed: {}", e))?;

    let mut data_in = CRYPTOAPI_BLOB {
        cbData: ciphertext.len() as u32,
        pbData: ciphertext.as_ptr() as *mut u8,
    };

    let mut data_out = CRYPTOAPI_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };

    let ok = unsafe {
        CryptUnprotectData(
            &mut data_in,
            std::ptr::null_mut(), // description out
            std::ptr::null_mut(), // optional entropy
            std::ptr::null_mut(), // reserved
            std::ptr::null_mut(), // prompt struct
            0,                    // flags
            &mut data_out,
        )
    };

    if ok == 0 {
        return Err("DPAPI CryptUnprotectData failed".to_string());
    }

    let decrypted =
        unsafe { std::slice::from_raw_parts(data_out.pbData, data_out.cbData as usize) };
    let plaintext = String::from_utf8_lossy(decrypted).to_string();

    unsafe {
        // Zero the raw plaintext bytes before freeing — prevents secrets
        // from lingering in freed memory.
        std::ptr::write_bytes(data_out.pbData, 0, data_out.cbData as usize);
        LocalFree(data_out.pbData as *mut _);
    }

    Ok(plaintext)
}

/// Encrypt a value if the key is in the sensitive list, otherwise pass through.
pub fn maybe_encrypt(key: &str, value: &str) -> String {
    if !SENSITIVE_KEYS.contains(&key) || value.is_empty() {
        return value.to_string();
    }
    match encrypt(value) {
        Ok(encrypted) => encrypted,
        Err(e) => {
            error!("DPAPI encrypt failed for key '{}': {} — storing plaintext", key, e);
            value.to_string()
        }
    }
}

/// Decrypt a value if the key is in the sensitive list, otherwise pass through.
pub fn maybe_decrypt(key: &str, value: &str) -> String {
    if !SENSITIVE_KEYS.contains(&key) || value.is_empty() {
        return value.to_string();
    }
    match decrypt(value) {
        Ok(plaintext) => plaintext,
        Err(e) => {
            error!("DPAPI decrypt failed for key '{}': {} — returning raw value", key, e);
            value.to_string()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_sensitive_keys_list() {
        assert!(SENSITIVE_KEYS.contains(&"auth_token"));
        assert!(SENSITIVE_KEYS.contains(&"auth_refresh_token"));
        assert!(SENSITIVE_KEYS.contains(&"api_key"));
        assert!(!SENSITIVE_KEYS.contains(&"username"));
        assert!(!SENSITIVE_KEYS.contains(&"theme"));
    }

    #[test]
    fn test_maybe_encrypt_non_sensitive_passthrough() {
        assert_eq!(maybe_encrypt("username", "alice"), "alice");
        assert_eq!(maybe_encrypt("theme", "dark"), "dark");
        assert_eq!(maybe_encrypt("display_name", "Bob"), "Bob");
    }

    #[test]
    fn test_maybe_encrypt_empty_value_passthrough() {
        // Empty values should pass through even for sensitive keys
        assert_eq!(maybe_encrypt("auth_token", ""), "");
        assert_eq!(maybe_encrypt("api_key", ""), "");
    }

    #[test]
    fn test_maybe_decrypt_non_sensitive_passthrough() {
        assert_eq!(maybe_decrypt("username", "alice"), "alice");
        assert_eq!(maybe_decrypt("theme", "dark"), "dark");
    }

    #[test]
    fn test_maybe_decrypt_empty_value_passthrough() {
        assert_eq!(maybe_decrypt("auth_token", ""), "");
        assert_eq!(maybe_decrypt("api_key", ""), "");
    }

    #[test]
    fn test_maybe_decrypt_plaintext_migration() {
        // Plaintext values (no dpapi: prefix) should pass through for migration
        let result = maybe_decrypt("auth_token", "my_plain_token");
        assert_eq!(result, "my_plain_token");
    }

    #[test]
    fn test_encrypt_produces_dpapi_prefix() {
        let encrypted = encrypt("hello world").expect("DPAPI encrypt should succeed");
        assert!(encrypted.starts_with("dpapi:"), "Expected dpapi: prefix, got: {}", encrypted);
    }

    #[test]
    fn test_encrypt_decrypt_roundtrip() {
        let original = "super_secret_token_12345";
        let encrypted = encrypt(original).expect("encrypt failed");
        let decrypted = decrypt(&encrypted).expect("decrypt failed");
        assert_eq!(decrypted, original);
    }

    #[test]
    fn test_maybe_encrypt_decrypt_roundtrip_sensitive() {
        let original = "jwt.token.value";
        let encrypted = maybe_encrypt("auth_token", original);
        assert!(encrypted.starts_with("dpapi:"));
        let decrypted = maybe_decrypt("auth_token", &encrypted);
        assert_eq!(decrypted, original);
    }

    #[test]
    fn test_decrypt_malformed_base64() {
        // dpapi: prefix with invalid base64 should return error
        let result = decrypt("dpapi:not-valid-base64!!!");
        assert!(result.is_err());
    }

    #[test]
    fn test_maybe_decrypt_malformed_returns_raw() {
        // maybe_decrypt should return the raw value on failure (not panic)
        let raw = "dpapi:not-valid-base64!!!";
        let result = maybe_decrypt("auth_token", raw);
        // Should return the raw value since DPAPI decrypt will fail on garbage
        assert_eq!(result, raw);
    }
}
