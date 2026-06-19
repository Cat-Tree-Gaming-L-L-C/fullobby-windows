use std::path::PathBuf;

use sha2::{Sha256, Digest};
use tracing::info;

use crate::api::client::HTTP_CLIENT;

/// Trusted domains for installer downloads.
const TRUSTED_DOWNLOAD_DOMAINS: &[&str] = &[
    "seeding-api.espritdecorpsgaming.org",
    "github.com",
    "objects.githubusercontent.com",
];

/// Validate that a download URL uses HTTPS and belongs to a trusted domain.
fn validate_download_url(url: &str) -> Result<(), String> {
    let parsed = reqwest::Url::parse(url).map_err(|e| format!("Invalid download URL: {e}"))?;

    if parsed.scheme() != "https" {
        return Err(format!("Download URL must use HTTPS, got: {}", parsed.scheme()));
    }

    let host = parsed.host_str().ok_or("Download URL has no host")?;
    if !TRUSTED_DOWNLOAD_DOMAINS.iter().any(|&d| host == d) {
        return Err(format!("Download URL host '{}' is not in the trusted domain list", host));
    }

    Ok(())
}

/// Check for application updates.
/// Reads the `update_channel` session key to determine whether to check
/// the stable (default) or beta channel.
pub async fn check_for_updates() -> Result<Option<UpdateInfo>, Box<dyn std::error::Error>> {
    let channel = crate::backend::session::get_stored_session("update_channel")
        .unwrap_or_default();
    let url = if channel == "beta" {
        "https://seeding-api.espritdecorpsgaming.org/api/releases/latest?channel=beta"
    } else {
        "https://seeding-api.espritdecorpsgaming.org/api/releases/latest"
    };

    let response = HTTP_CLIENT.get(url).send().await?;

    if !response.status().is_success() {
        return Err(format!("Update check failed: {}", response.status()).into());
    }

    let release: serde_json::Value = response.json().await?;

    let latest_version = release
        .get("version")
        .and_then(|v| v.as_str())
        .unwrap_or("");

    let current_version = env!("CARGO_PKG_VERSION");

    if latest_version != current_version && !latest_version.is_empty() {
        info!(
            "Update available: {} -> {}",
            current_version, latest_version
        );
        Ok(Some(UpdateInfo {
            version: latest_version.to_string(),
            download_url: release
                .get("download_url")
                .or_else(|| release.get("url"))
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string(),
            notes: release
                .get("notes")
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string(),
            sha256: release
                .get("sha256")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string()),
            signature: release
                .get("signature")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string()),
        }))
    } else {
        info!("No updates available (current: {})", current_version);
        Ok(None)
    }
}

/// Validate that an installer file extension is safe to launch.
fn validate_installer_extension(extension: &str) -> Result<(), String> {
    match extension.to_lowercase().as_str() {
        "exe" | "msi" => Ok(()),
        other => Err(format!(
            "Refusing to launch installer with unexpected extension: '{}'",
            other
        )),
    }
}

/// Sanitize an installer filename by stripping path separators and parent-directory references.
/// Falls back to a safe default name if the result is empty.
fn sanitize_installer_filename(raw_name: &str) -> String {
    let sanitized: String = raw_name
        .replace(['/', '\\'], "")
        .replace("..", "");
    if sanitized.is_empty() {
        "esprit-seeder-update.exe".to_string()
    } else {
        sanitized
    }
}

/// Verify a SHA-256 checksum against downloaded bytes.
/// Returns Ok(()) if the hash matches, Err with a descriptive message otherwise.
fn verify_sha256(bytes: &[u8], expected: &str) -> Result<(), String> {
    if expected.len() != 64 {
        return Err(format!(
            "Invalid SHA-256 hash length: {} (expected 64 hex chars)",
            expected.len()
        ));
    }
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    let actual = format!("{:x}", hasher.finalize());
    if actual.eq_ignore_ascii_case(expected) {
        Ok(())
    } else {
        Err(format!(
            "Checksum mismatch: expected {}, got {}",
            expected, actual
        ))
    }
}

/// Validate that an installer download size is within limits.
/// Returns Ok(()) if within 500 MB, Err otherwise.
fn validate_installer_size(len: usize) -> Result<(), String> {
    const MAX_INSTALLER_SIZE: usize = 500 * 1024 * 1024; // 500 MB
    if len > MAX_INSTALLER_SIZE {
        Err(format!(
            "Download too large: {} bytes (max {})",
            len, MAX_INSTALLER_SIZE
        ))
    } else {
        Ok(())
    }
}

/// Download the update installer and launch it.
///
/// Downloads to a temp directory, then spawns the installer process and exits
/// so the installer can replace the running exe.
pub async fn download_and_install(update: &UpdateInfo) -> Result<(), Box<dyn std::error::Error>> {
    if update.download_url.is_empty() {
        return Err("No download URL available".into());
    }

    validate_download_url(&update.download_url)
        .map_err(|e| -> Box<dyn std::error::Error> { e.into() })?;

    info!("Downloading update from: {}", update.download_url);

    let response = HTTP_CLIENT.get(&update.download_url).timeout(std::time::Duration::from_secs(300)).send().await?;

    if !response.status().is_success() {
        return Err(format!("Download failed: {}", response.status()).into());
    }

    // Determine file name from URL, sanitizing to prevent path traversal
    let raw_name = update
        .download_url
        .rsplit('/')
        .next()
        .unwrap_or("esprit-seeder-update.exe");

    let file_name = sanitize_installer_filename(raw_name);

    let temp_dir = std::env::temp_dir().join("esprit-seeder-update");
    tokio::fs::create_dir_all(&temp_dir).await?;
    let download_path = temp_dir.join(&file_name);

    let bytes = response.bytes().await?;

    // Enforce file size limit to prevent disk exhaustion
    validate_installer_size(bytes.len())
        .map_err(|e| -> Box<dyn std::error::Error> { e.into() })?;

    // Require SHA-256 checksum — refuse to install unverified binaries
    if let Some(ref expected_hash) = update.sha256 {
        verify_sha256(&bytes, expected_hash)
            .map_err(|e| -> Box<dyn std::error::Error> { e.into() })?;
        info!("Installer checksum verified (SHA-256: {})", expected_hash);
    } else {
        return Err("Server did not provide SHA-256 checksum — refusing download".into());
    }

    // Warn if no cryptographic signature — update integrity relies solely on SHA-256 + HTTPS
    if update.signature.is_none() {
        tracing::warn!(
            "Update manifest has no Ed25519 signature — integrity relies on SHA-256 + HTTPS. \
             Configure release signing for defense-in-depth against server compromise."
        );
    }

    tokio::fs::write(&download_path, &bytes).await?;

    info!(
        "Update downloaded to: {} ({} bytes)",
        download_path.display(),
        bytes.len()
    );

    launch_installer(&download_path)?;

    Ok(())
}

/// Launch the downloaded installer and exit the current process.
/// Only allows `.exe` and `.msi` extensions — rejects anything else.
fn launch_installer(path: &PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    let extension = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();

    validate_installer_extension(&extension)?;

    info!("Launching installer: {}", path.display());
    std::process::Command::new(path).spawn()?;

    // Give the installer a moment to start, then exit
    info!("Update installer launched, exiting for update...");
    crate::config::flush_pending_saves();
    crate::backend::heartbeat::stop_heartbeat_sync(Some("update".into()));
    crate::platform::tray::close_app();
    Ok(())
}

#[derive(Debug, Clone)]
pub struct UpdateInfo {
    pub version: String,
    pub download_url: String,
    pub notes: String,
    /// Optional SHA-256 hash of the installer binary for integrity verification.
    /// When present, the downloaded file is verified before launching.
    pub sha256: Option<String>,
    /// Optional Ed25519 signature of the update manifest for authenticity verification.
    /// When signing infrastructure is configured, this verifies the manifest was issued
    /// by a trusted release authority, not just any server compromise.
    pub signature: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    // ─── validate_installer_extension ───────────────────────────────

    #[test]
    fn test_extension_exe() {
        assert!(validate_installer_extension("exe").is_ok());
    }

    #[test]
    fn test_extension_msi() {
        assert!(validate_installer_extension("msi").is_ok());
    }

    #[test]
    fn test_extension_case_insensitive() {
        assert!(validate_installer_extension("EXE").is_ok());
        assert!(validate_installer_extension("MSI").is_ok());
        assert!(validate_installer_extension("Exe").is_ok());
    }

    #[test]
    fn test_extension_rejected() {
        assert!(validate_installer_extension("bat").is_err());
        assert!(validate_installer_extension("ps1").is_err());
        assert!(validate_installer_extension("cmd").is_err());
        assert!(validate_installer_extension("sh").is_err());
        assert!(validate_installer_extension("").is_err());
    }

    // ─── sanitize_installer_filename ────────────────────────────────

    #[test]
    fn test_sanitize_normal_name() {
        assert_eq!(sanitize_installer_filename("setup.exe"), "setup.exe");
    }

    #[test]
    fn test_sanitize_path_traversal() {
        assert_eq!(
            sanitize_installer_filename("../../../etc/passwd"),
            "etcpasswd"
        );
    }

    #[test]
    fn test_sanitize_backslash_traversal() {
        assert_eq!(
            sanitize_installer_filename(r"..\..\setup.exe"),
            "setup.exe"
        );
    }

    #[test]
    fn test_sanitize_dots_and_slashes_only() {
        // ".." gets removed, "/" and "\" get removed → empty → fallback
        assert_eq!(
            sanitize_installer_filename("../\\"),
            "esprit-seeder-update.exe"
        );
    }

    #[test]
    fn test_sanitize_empty() {
        assert_eq!(
            sanitize_installer_filename(""),
            "esprit-seeder-update.exe"
        );
    }

    // ─── verify_sha256 ─────────────────────────────────────────────

    #[test]
    fn test_verify_sha256_valid() {
        let data = b"hello world";
        // Known SHA-256 of "hello world"
        let expected = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";
        assert!(verify_sha256(data, expected).is_ok());
    }

    #[test]
    fn test_verify_sha256_mismatch() {
        let data = b"hello world";
        let wrong = "0000000000000000000000000000000000000000000000000000000000000000";
        let result = verify_sha256(data, wrong);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("Checksum mismatch"));
    }

    #[test]
    fn test_verify_sha256_wrong_length() {
        let data = b"hello";
        let result = verify_sha256(data, "abc123");
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("Invalid SHA-256 hash length"));
    }

    #[test]
    fn test_verify_sha256_case_insensitive() {
        let data = b"hello world";
        let upper = "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9";
        assert!(verify_sha256(data, upper).is_ok());
    }

    #[test]
    fn test_verify_sha256_mixed_case() {
        let data = b"hello world";
        let mixed = "B94d27b9934D3e08A52e52d7DA7dabfAC484efe37A5380ee9088F7ace2EFCDE9";
        assert!(verify_sha256(data, mixed).is_ok());
    }

    #[test]
    fn test_verify_sha256_empty_input() {
        let data = b"";
        // SHA-256 of empty string
        let expected = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        assert!(verify_sha256(data, expected).is_ok());
    }

    // ─── validate_installer_size ────────────────────────────────────

    #[test]
    fn test_size_under_limit() {
        assert!(validate_installer_size(100 * 1024 * 1024).is_ok()); // 100 MB
    }

    #[test]
    fn test_size_at_limit() {
        assert!(validate_installer_size(500 * 1024 * 1024).is_ok()); // exactly 500 MB
    }

    #[test]
    fn test_size_over_limit() {
        let result = validate_installer_size(500 * 1024 * 1024 + 1);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("too large"));
    }

    #[test]
    fn test_size_zero() {
        assert!(validate_installer_size(0).is_ok());
    }

    // ─── validate_download_url ────────────────────────────────────────

    #[test]
    fn test_download_url_trusted_domains() {
        assert!(validate_download_url("https://seeding-api.espritdecorpsgaming.org/releases/v1.0.exe").is_ok());
        assert!(validate_download_url("https://github.com/org/repo/releases/download/v1/setup.exe").is_ok());
        assert!(validate_download_url("https://objects.githubusercontent.com/path/to/file").is_ok());
    }

    #[test]
    fn test_download_url_rejects_http() {
        let result = validate_download_url("http://seeding-api.espritdecorpsgaming.org/file.exe");
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("HTTPS"));
    }

    #[test]
    fn test_download_url_rejects_untrusted_domain() {
        let result = validate_download_url("https://evil.com/malware.exe");
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("not in the trusted domain list"));
    }

    #[test]
    fn test_download_url_rejects_invalid_url() {
        assert!(validate_download_url("not-a-url").is_err());
        assert!(validate_download_url("").is_err());
    }
}
