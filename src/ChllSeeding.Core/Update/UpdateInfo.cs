namespace ChllSeeding.Core.Update;

/// <summary>
/// A pending application update parsed from the <c>/api/releases/latest</c> manifest.
/// Port of the Rust <c>platform::updater::UpdateInfo</c>.
/// </summary>
public sealed record UpdateInfo
{
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public string Notes { get; init; } = "";

    /// <summary>SHA-256 of the installer binary. Required before launch — an update with no
    /// checksum is refused (the download path treats a missing hash as a hard error).</summary>
    public string? Sha256 { get; init; }

    /// <summary>Optional Ed25519 signature of the manifest for authenticity (defense-in-depth
    /// beyond SHA-256 + HTTPS). A missing signature only warns; it does not block.</summary>
    public string? Signature { get; init; }
}
