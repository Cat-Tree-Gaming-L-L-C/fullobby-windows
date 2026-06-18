using System.Security.Cryptography;

namespace ChllSeeding.Core.Update;

/// <summary>
/// Pure validation helpers for the self-updater, ported from the <c>#[cfg(test)]</c>-backed
/// functions in <c>src-rust/src/platform/updater.rs</c>: download-URL trust checks, installer
/// extension/filename sanitisation, SHA-256 verification, and size limits. Kept side-effect-free
/// so the Rust test coverage carries over 1:1 (see <c>UpdateValidationTests</c>).
/// </summary>
public static class UpdateValidation
{
    /// <summary>Maximum installer download size (500 MB), matching the Rust limit.</summary>
    public const long MaxInstallerSize = 500L * 1024 * 1024;

    /// <summary>GitHub release-asset hosts that are always trusted (the configured API host is
    /// added on top — see <see cref="UpdaterService.TrustedDownloadDomains"/>).</summary>
    public static readonly string[] GitHubDownloadDomains = ["github.com", "objects.githubusercontent.com"];

    /// <summary>Validate that a download URL uses HTTPS and belongs to a trusted host.
    /// Returns <c>null</c> on success or a human-readable error message on failure
    /// (port of <c>validate_download_url</c>).</summary>
    public static string? ValidateDownloadUrl(string url, IReadOnlyCollection<string> trustedDomains)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return $"Invalid download URL: {url}";
        }

        if (parsed.Scheme != "https")
        {
            return $"Download URL must use HTTPS, got: {parsed.Scheme}";
        }

        var host = parsed.Host;
        if (string.IsNullOrEmpty(host))
        {
            return "Download URL has no host";
        }

        if (!trustedDomains.Any(d => string.Equals(host, d, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Download URL host '{host}' is not in the trusted domain list";
        }

        return null;
    }

    /// <summary>Validate that an installer file extension is safe to launch (only <c>exe</c>/<c>msi</c>).
    /// Returns <c>null</c> on success or an error message (port of <c>validate_installer_extension</c>).</summary>
    public static string? ValidateInstallerExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            "exe" or "msi" => null,
            var other => $"Refusing to launch installer with unexpected extension: '{other}'",
        };

    /// <summary>Strip path separators and parent-directory references from an installer filename,
    /// falling back to a safe default when the result is empty (port of
    /// <c>sanitize_installer_filename</c>).</summary>
    public static string SanitizeInstallerFilename(string rawName)
    {
        var sanitized = rawName
            .Replace("/", "")
            .Replace("\\", "")
            .Replace("..", "");
        return sanitized.Length == 0 ? DefaultInstallerName : sanitized;
    }

    /// <summary>Default installer filename used when the URL-derived name sanitises to empty
    /// (rebranded from <c>esprit-seeder-update.exe</c>).</summary>
    public const string DefaultInstallerName = "chll-seeding-update.exe";

    /// <summary>Verify a SHA-256 checksum (64 hex chars, case-insensitive) against downloaded bytes.
    /// Returns <c>null</c> on a match or an error message (port of <c>verify_sha256</c>).</summary>
    public static string? VerifySha256(byte[] bytes, string expected)
    {
        if (expected.Length != 64)
        {
            return $"Invalid SHA-256 hash length: {expected.Length} (expected 64 hex chars)";
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Checksum mismatch: expected {expected}, got {actual}";
    }

    /// <summary>Validate that an installer download size is within the 500 MB limit.
    /// Returns <c>null</c> on success or an error message (port of <c>validate_installer_size</c>).</summary>
    public static string? ValidateInstallerSize(long length) =>
        length > MaxInstallerSize
            ? $"Download too large: {length} bytes (max {MaxInstallerSize})"
            : null;

    /// <summary>Whether a newer version is available, mirroring the Rust string-inequality check
    /// (<c>latest != current &amp;&amp; !latest.is_empty()</c>).</summary>
    public static bool IsUpdateAvailable(string currentVersion, string latestVersion) =>
        latestVersion.Length > 0 && latestVersion != currentVersion;
}
