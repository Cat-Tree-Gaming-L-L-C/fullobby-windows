namespace ChllSeeding.Core.Update;

/// <summary>
/// A pending application update parsed from the <c>/api/releases/latest</c> manifest.
/// Metadata for an available update (version, download URL, checksum, signature, notes).
/// </summary>
public sealed record UpdateInfo
{
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public string Notes { get; init; } = "";

    /// <summary>SHA-256 of the installer binary. Required before launch — an update with no
    /// checksum is refused (the download path treats a missing hash as a hard error).</summary>
    public string? Sha256 { get; init; }

    /// <summary>Base64 ECDSA P-256 / SHA-256 signature over the canonical <c>(version, sha256)</c>
    /// payload (see <see cref="UpdateSignature"/>), produced offline by the release-signing key.
    /// <b>Mandatory</b>: the updater refuses any update whose signature is missing or does not verify
    /// against the client's hardbaked public key — this is what defends against a compromised
    /// update origin, which SHA-256 + HTTPS alone cannot.</summary>
    public string? Signature { get; init; }
}
