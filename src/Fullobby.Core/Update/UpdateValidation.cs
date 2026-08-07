using System.Security.Cryptography;

namespace Fullobby.Core.Update;

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

    private const string FallbackInstallerName = "fullobby-update.exe";

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

    /// <summary>Strip every OS-invalid filename character (path separators, <c>:</c>, wildcards,
    /// control chars) and parent-directory references from a download filename, falling back to a
    /// safe default when nothing usable remains. Stripping <c>:</c> matters on Windows: a name like
    /// <c>c:evil.exe</c> is drive-relative, so <see cref="System.IO.Path.Combine(string, string)"/>
    /// would treat it as rooted and escape the intended temp directory.</summary>
    public static string SanitizeInstallerFilename(string rawName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stripped = new string(rawName.Where(c => Array.IndexOf(invalid, c) < 0).ToArray());
        var sanitized = stripped.Replace("..", "");
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

    /// <summary>An update is available only when <paramref name="latestVersion"/> is a valid semver
    /// that is <em>strictly greater</em> than <paramref name="currentVersion"/>. This is an
    /// anti-rollback guard: signatures authenticate that a release is genuine, but a controlled or
    /// replayed manifest feed could still serve a real, older, signed release to force a downgrade to a
    /// known-vulnerable build. Requiring strictly-newer ordering (rather than mere inequality) closes
    /// that. Unparseable versions fail closed — no update is offered — so a malformed feed can never
    /// trigger an install.</summary>
    public static bool IsUpdateAvailable(string currentVersion, string latestVersion)
    {
        if (!TryParseSemVer(latestVersion, out var latest) ||
            !TryParseSemVer(currentVersion, out var current))
        {
            return false;
        }
        return CompareSemVer(latest, current) > 0;
    }

    /// <summary>A parsed semantic version: numeric core plus optional dot-separated pre-release
    /// identifiers. Build metadata (<c>+…</c>) is ignored — it does not affect precedence.</summary>
    private readonly record struct SemVer(int Major, int Minor, int Patch, string[] PreRelease);

    /// <summary>Parse a <c>MAJOR.MINOR.PATCH[-prerelease][+build]</c> string (a leading <c>v</c> is
    /// tolerated). Returns <c>false</c> for anything that is not a well-formed semver core.</summary>
    private static bool TryParseSemVer(string value, out SemVer semver)
    {
        semver = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Strip build metadata; it is not part of precedence.
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string[] pre = [];
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            var preText = text[(dash + 1)..];
            text = text[..dash];
            if (preText.Length == 0)
            {
                return false; // trailing '-' with no identifiers
            }
            pre = preText.Split('.');
            if (Array.Exists(pre, p => p.Length == 0))
            {
                return false; // empty identifier, e.g. "1.0.0-beta..1"
            }
        }

        var core = text.Split('.');
        if (core.Length != 3)
        {
            return false;
        }
        if (!int.TryParse(core[0], out var major) || major < 0 ||
            !int.TryParse(core[1], out var minor) || minor < 0 ||
            !int.TryParse(core[2], out var patch) || patch < 0)
        {
            return false;
        }

        semver = new SemVer(major, minor, patch, pre);
        return true;
    }

    /// <summary>Compare two semvers by precedence (semver.org §11): numeric core first, then a version
    /// <em>with</em> a pre-release ranks below the same core <em>without</em> one, then pre-release
    /// identifiers left-to-right (numeric &lt; alphanumeric; more identifiers wins on a common prefix).
    /// Returns &lt;0, 0, or &gt;0.</summary>
    private static int CompareSemVer(SemVer a, SemVer b)
    {
        var core = a.Major.CompareTo(b.Major);
        if (core != 0) return core;
        core = a.Minor.CompareTo(b.Minor);
        if (core != 0) return core;
        core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;

        // A pre-release version has lower precedence than the associated normal release.
        if (a.PreRelease.Length == 0 && b.PreRelease.Length == 0) return 0;
        if (a.PreRelease.Length == 0) return 1;
        if (b.PreRelease.Length == 0) return -1;

        var shared = Math.Min(a.PreRelease.Length, b.PreRelease.Length);
        for (var i = 0; i < shared; i++)
        {
            var cmp = ComparePreReleaseIdentifier(a.PreRelease[i], b.PreRelease[i]);
            if (cmp != 0) return cmp;
        }
        return a.PreRelease.Length.CompareTo(b.PreRelease.Length);
    }

    /// <summary>Compare one pre-release identifier: all-numeric identifiers compare numerically and
    /// rank below alphanumeric ones; otherwise ASCII lexical order.</summary>
    private static int ComparePreReleaseIdentifier(string a, string b)
    {
        var aNum = int.TryParse(a, out var an);
        var bNum = int.TryParse(b, out var bn);
        if (aNum && bNum) return an.CompareTo(bn);
        if (aNum) return -1; // numeric identifiers have lower precedence than alphanumeric
        if (bNum) return 1;
        return string.CompareOrdinal(a, b);
    }
}
