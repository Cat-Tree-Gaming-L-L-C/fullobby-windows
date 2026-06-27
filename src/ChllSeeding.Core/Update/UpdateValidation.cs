using System.Security.Cryptography;

namespace ChllSeeding.Core.Update;

/// <summary>
/// Pure validation + sanitization helpers for the self-updater. Split out from
/// <see cref="UpdaterService"/> so the security-critical checks are unit-testable without any
/// network or filesystem. Each <c>Validate*</c> returns <c>null</c> when the input is acceptable,
/// otherwise a human-readable error message.
/// </summary>
public static class UpdateValidation
{
    /// <summary>Installer downloads may exceed this neither in the Content-Length nor the body (500 MB).</summary>
    public const long MaxInstallerSize = 500L * 1024 * 1024;

    private const string FallbackInstallerName = "chll-seeding-update.exe";

    /// <summary>Validate that a download URL uses HTTPS and its host is in the trusted set.</summary>
    public static string? ValidateDownloadUrl(string url, IReadOnlyCollection<string> trustedHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return $"Invalid download URL: {url}";
        }
        if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return $"Download URL must use HTTPS, got: {parsed.Scheme}";
        }
        var host = parsed.Host;
        if (host.Length == 0)
        {
            return "Download URL has no host";
        }
        if (!trustedHosts.Any(d => string.Equals(host, d, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Download URL host '{host}' is not in the trusted domain list";
        }
        return null;
    }

    /// <summary>Only <c>.exe</c> and <c>.msi</c> are safe to launch; anything else is rejected.</summary>
    public static string? ValidateInstallerExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            "exe" or "msi" => null,
            _ => $"Refusing to launch installer with unexpected extension: '{extension}'",
        };

    /// <summary>Strip path separators and parent-directory references from a download filename,
    /// falling back to a safe default when nothing usable remains.</summary>
    public static string SanitizeInstallerFilename(string rawName)
    {
        var sanitized = rawName.Replace("/", "").Replace("\\", "").Replace("..", "");
        return sanitized.Length == 0 ? FallbackInstallerName : sanitized;
    }

    /// <summary>Verify a SHA-256 checksum (64 hex chars, case-insensitive) against the bytes.</summary>
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

    /// <summary>Reject downloads larger than <see cref="MaxInstallerSize"/> (disk-exhaustion guard).</summary>
    public static string? ValidateInstallerSize(long length) =>
        length > MaxInstallerSize
            ? $"Download too large: {length} bytes (max {MaxInstallerSize})"
            : null;

    /// <summary>An update is available when the latest version is non-empty and differs from the
    /// current one (string inequality — the server decides ordering).</summary>
    public static bool IsUpdateAvailable(string currentVersion, string latestVersion) =>
        latestVersion.Length > 0 && latestVersion != currentVersion;
}
