using System.Reflection;
using System.Text.Json;
using ChllSeeding.Core.Api;
using ChllSeeding.Core.Config;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Update;

/// <summary>Metadata for an available update (port of the Rust <c>UpdateInfo</c> struct).</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes, string? Sha256, string? Signature);

/// <summary>
/// Self-updater — port of <c>src-rust/src/platform/updater.rs</c>. Checks the
/// <c>/api/releases/latest</c> endpoint (honoring the stable/beta <c>update_channel</c> config
/// key), and downloads + verifies (HTTPS, trusted host, SHA-256, ≤500 MB, safe extension) the Inno
/// <c>setup.exe</c> before launching it. The caller is responsible for shutting the app down once
/// the installer has started (the installer must replace the running exe).
/// </summary>
public sealed class UpdaterService(
    IHttpClientFactory httpFactory,
    ConfigService config,
    ILogger<UpdaterService> log)
{
    /// <summary>Named <see cref="HttpClient"/> with a long timeout (installer downloads can be large)
    /// and resilience, but no auth handler — release endpoints are public.</summary>
    public const string ClientName = "updater";

    /// <summary>HTTPS hosts permitted for installer downloads: the configured API host plus GitHub's
    /// release-asset CDNs. Derived from <see cref="ApiConfig.BaseUrl"/> so the env override is honored.</summary>
    public static IReadOnlyList<string> TrustedDownloadDomains
    {
        get
        {
            var domains = new List<string>();
            if (Uri.TryCreate(ApiConfig.BaseUrl, UriKind.Absolute, out var apiUri) && apiUri.Host.Length > 0)
            {
                domains.Add(apiUri.Host);
            }
            domains.AddRange(UpdateValidation.GitHubDownloadDomains);
            return domains;
        }
    }

    /// <summary>The currently-running app version, formatted like the Rust <c>CARGO_PKG_VERSION</c>
    /// (major.minor.patch). Read from the entry assembly, falling back to this assembly.</summary>
    public static string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Check for an available update on the configured channel. Returns <c>null</c> when
    /// already up to date. Throws on network/HTTP failure (port of <c>check_for_updates</c>).</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var channel = config.GetString("update_channel") ?? "";
        var url = channel == "beta"
            ? $"{ApiConfig.BaseUrl}/api/releases/latest?channel=beta"
            : $"{ApiConfig.BaseUrl}/api/releases/latest";

        var client = httpFactory.CreateClient(ClientName);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Update check failed: {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var release = doc.RootElement;

        var latestVersion = GetStr(release, "version") ?? "";
        var current = CurrentVersion;

        if (!UpdateValidation.IsUpdateAvailable(current, latestVersion))
        {
            log.LogInformation("No updates available (current: {Version})", current);
            return null;
        }

        log.LogInformation("Update available: {Current} -> {Latest}", current, latestVersion);
        return new UpdateInfo(
            Version: latestVersion,
            DownloadUrl: GetStr(release, "download_url") ?? GetStr(release, "url") ?? "",
            Notes: GetStr(release, "notes") ?? "",
            Sha256: GetStr(release, "sha256"),
            Signature: GetStr(release, "signature"));
    }

    /// <summary>Download the installer, verify it (HTTPS + trusted host + SHA-256 + size + extension),
    /// write it to a temp dir, and launch it. Returns the launched installer path. The caller must
    /// then shut the app down so the installer can replace the running exe (port of
    /// <c>download_and_install</c> minus the in-process shutdown, which the App layer owns).</summary>
    public async Task<string> DownloadAndInstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(update.DownloadUrl))
        {
            throw new InvalidOperationException("No download URL available");
        }

        if (UpdateValidation.ValidateDownloadUrl(update.DownloadUrl, TrustedDownloadDomains) is { } urlError)
        {
            throw new InvalidOperationException(urlError);
        }

        log.LogInformation("Downloading update from: {Url}", update.DownloadUrl);

        var client = httpFactory.CreateClient(ClientName);
        using var response = await client.GetAsync(update.DownloadUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Download failed: {(int)response.StatusCode}");
        }

        var rawName = update.DownloadUrl.Split('/').LastOrDefault() ?? UpdateValidation.DefaultInstallerName;
        var fileName = UpdateValidation.SanitizeInstallerFilename(rawName);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // Enforce the size limit to prevent disk exhaustion.
        if (UpdateValidation.ValidateInstallerSize(bytes.LongLength) is { } sizeError)
        {
            throw new InvalidOperationException(sizeError);
        }

        // Require a SHA-256 checksum — refuse to install unverified binaries.
        if (update.Sha256 is { } expectedHash)
        {
            if (UpdateValidation.VerifySha256(bytes, expectedHash) is { } hashError)
            {
                throw new InvalidOperationException(hashError);
            }
            log.LogInformation("Installer checksum verified (SHA-256: {Hash})", expectedHash);
        }
        else
        {
            throw new InvalidOperationException("Server did not provide SHA-256 checksum — refusing download");
        }

        if (update.Signature is null)
        {
            log.LogWarning(
                "Update manifest has no signature — integrity relies on SHA-256 + HTTPS. " +
                "Configure release signing for defense-in-depth against server compromise.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "chll-seeding-update");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, fileName);
        await File.WriteAllBytesAsync(downloadPath, bytes, ct).ConfigureAwait(false);

        log.LogInformation("Update downloaded to: {Path} ({Bytes} bytes)", downloadPath, bytes.LongLength);

        LaunchInstaller(downloadPath);
        return downloadPath;
    }

    /// <summary>Launch the downloaded installer, allowing only <c>.exe</c>/<c>.msi</c>
    /// (port of <c>launch_installer</c>; process shutdown is left to the caller).</summary>
    private void LaunchInstaller(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (UpdateValidation.ValidateInstallerExtension(extension) is { } extError)
        {
            throw new InvalidOperationException(extError);
        }

        log.LogInformation("Launching installer: {Path}", path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string? GetStr(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
