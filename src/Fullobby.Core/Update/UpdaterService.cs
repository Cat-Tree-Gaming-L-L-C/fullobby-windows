using System.Diagnostics;
using System.Text.Json;
using Fullobby.Core.Api;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Update;

/// <summary>
/// Self-updater: fetches the signed release manifest from the static update feed
/// (<see cref="UpdateConfig.FeedBaseUrl"/> — GitHub Pages, not the API), downloads the installer to a
/// temp directory with HTTPS + trusted-domain + size + SHA-256 validation, and launches it.
/// The security-critical checks live in
/// <see cref="UpdateValidation"/> (unit-tested); this class owns the network + filesystem I/O.
///
/// The post-launch app shutdown (flush config, stop heartbeat, exit so the installer can replace
/// the running exe) is deliberately left to the App layer — Core stays UI-/lifecycle-free.
/// </summary>
public sealed class UpdaterService
{
    /// <summary>Named HttpClient for update checks + installer downloads (generous timeout, no auth).</summary>
    public const string HttpClientName = "updater";

    private readonly ILogger<UpdaterService> _log;
    private readonly IHttpClientFactory _httpFactory;

    public UpdaterService(ILogger<UpdaterService> log, IHttpClientFactory httpFactory)
    {
        _log = log;
        _httpFactory = httpFactory;
    }

    /// <summary>Hosts an installer download may come from: the configured API host plus GitHub's
    /// release CDNs. Derived from <see cref="ApiConfig.BaseUrl"/> so a staging override still works.</summary>
    public static IReadOnlyCollection<string> TrustedDownloadHosts()
    {
        var hosts = new List<string> { "github.com", "objects.githubusercontent.com" };
        if (Uri.TryCreate(ApiConfig.BaseUrl, UriKind.Absolute, out var api) && api.Host.Length > 0)
        {
            hosts.Add(api.Host);
        }
        return hosts;
    }

    /// <summary>Static manifest filenames on the update feed, per channel.</summary>
    private const string StableManifest = "latest.json";
    private const string BetaManifest = "latest-beta.json";

    /// <summary>
    /// Check for an available update on the given channel. <paramref name="channel"/> is the stored
    /// <c>update_channel</c> config value — "beta" fetches <c>latest-beta.json</c>, anything else
    /// <c>latest.json</c>, from the static update feed. Returns <c>null</c> when already current.
    /// Throws on a network/HTTP failure.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(
        string currentVersion, string? channel, CancellationToken ct = default)
    {
        var manifest = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? BetaManifest
            : StableManifest;
        var url = $"{UpdateConfig.FeedBaseUrl}/{manifest}";

        var client = _httpFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Update check failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var latest = GetString(root, "version") ?? "";
        if (!UpdateValidation.IsUpdateAvailable(currentVersion, latest))
        {
            _log.LogInformation("No updates available (current: {Current})", currentVersion);
            return null;
        }

        _log.LogInformation("Update available: {Current} -> {Latest}", currentVersion, latest);
        return new UpdateInfo
        {
            Version = latest,
            DownloadUrl = GetString(root, "download_url") ?? GetString(root, "url") ?? "",
            Notes = GetString(root, "notes") ?? "",
            Sha256 = GetString(root, "sha256"),
            Signature = GetString(root, "signature"),
        };
    }

    /// <summary>
    /// Download the installer to a temp directory and validate it: a trusted pinned-key signature
    /// over <c>(version, sha256)</c>, HTTPS + trusted host, ≤500 MB, and a matching SHA-256 (a missing
    /// signature or checksum is a hard failure). Returns the written path. Throws on any validation or
    /// I/O failure — the caller must not launch on a throw.
    /// </summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (update.DownloadUrl.Length == 0)
        {
            throw new InvalidOperationException("No download URL available");
        }

        // Authenticate the (version, sha256) binding against the hardbaked release-signing key BEFORE
        // spending bandwidth on the installer. The private key is held offline, so a compromised
        // server/API cannot forge this — without a valid signature we refuse the update entirely. The
        // SHA-256 check further down then ties this authenticated hash to the actual installer bytes.
        if (UpdateSignature.VerifySignature(update.Version, update.Sha256 ?? "", update.Signature) is { } sigErr)
        {
            throw new InvalidOperationException($"Update signature verification failed: {sigErr}");
        }
        _log.LogInformation("Update signature verified for v{Version}", update.Version);

        var urlError = UpdateValidation.ValidateDownloadUrl(update.DownloadUrl, TrustedDownloadHosts());
        if (urlError is not null)
        {
            throw new InvalidOperationException(urlError);
        }

        _log.LogInformation("Downloading update from: {Url}", update.DownloadUrl);

        var client = _httpFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(
            update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        // Cheap early reject before buffering a multi-hundred-MB body.
        var declared = response.Content.Headers.ContentLength;
        if (declared is { } len && UpdateValidation.ValidateInstallerSize(len) is { } sizeErr)
        {
            throw new InvalidOperationException(sizeErr);
        }

        var rawName = update.DownloadUrl.Split('/').LastOrDefault() ?? "";
        var fileName = UpdateValidation.SanitizeInstallerFilename(rawName);

        // Fresh randomized subdirectory per download so the installer path is unpredictable —
        // a same-user process can't pre-plant a file at a known location for us to launch.
        var tempDir = Path.Combine(Path.GetTempPath(), "fullobby-update", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, fileName);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        if (UpdateValidation.ValidateInstallerSize(bytes.Length) is { } actualSizeErr)
        {
            throw new InvalidOperationException(actualSizeErr);
        }

        // Require a SHA-256 — refuse to install an unverified binary.
        if (update.Sha256 is { Length: > 0 } expected)
        {
            if (UpdateValidation.VerifySha256(bytes, expected) is { } hashErr)
            {
                throw new InvalidOperationException(hashErr);
            }
            _log.LogInformation("Installer checksum verified (SHA-256: {Hash})", expected);
        }
        else
        {
            throw new InvalidOperationException("Server did not provide SHA-256 checksum — refusing download");
        }

        await File.WriteAllBytesAsync(downloadPath, bytes, ct).ConfigureAwait(false);
        _log.LogInformation("Update downloaded to: {Path} ({Bytes} bytes)", downloadPath, bytes.Length);
        return downloadPath;
    }

    /// <summary>
    /// Launch the downloaded installer (only <c>.exe</c>/<c>.msi</c> are allowed). The on-disk bytes
    /// are re-verified against <paramref name="expectedSha256"/> immediately before launch to close the
    /// TOCTOU gap between download and execution. Returns once the process is spawned; the caller is
    /// responsible for shutting the app down afterwards so the installer can replace the running exe.
    /// </summary>
    public void LaunchInstaller(string path, string expectedSha256)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (UpdateValidation.ValidateInstallerExtension(extension) is { } extError)
        {
            throw new InvalidOperationException(extError);
        }

        // Re-hash what's actually on disk now. DownloadAndVerifyAsync verified the in-memory buffer it
        // wrote, but a same-user process could have swapped the file in the interim — never launch
        // bytes we haven't just verified.
        var onDisk = File.ReadAllBytes(path);
        if (UpdateValidation.VerifySha256(onDisk, expectedSha256) is { } hashErr)
        {
            throw new InvalidOperationException($"Installer failed re-verification before launch: {hashErr}");
        }

        _log.LogInformation("Launching installer: {Path}", path);
        // UseShellExecute so both .exe and .msi launch via their shell association.
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        _log.LogInformation("Update installer launched, app should exit for the update to apply");
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
