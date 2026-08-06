using System.Security.Cryptography;
using System.Text;

namespace Fullobby.Core.Update;

/// <summary>
/// Authenticity check for self-update releases. Verifies an <b>ECDSA P-256 / SHA-256</b> signature
/// over a canonical <c>(version, sha256)</c> payload against a public key <b>hardbaked into the
/// client</b>. The matching private key lives only on an offline signing device — never in CI or on
/// the API/server — so a compromised update origin cannot forge a release it cannot sign.
///
/// <para>This is the layer that <see cref="UpdateValidation"/>'s SHA-256 check alone cannot provide:
/// HTTPS + a server-supplied hash only stop in-transit tampering, not a malicious origin serving
/// malware plus its matching hash. Binding the hash to a pinned-key signature closes that gap.</para>
///
/// <para>Pure and unit-testable: no network or filesystem. Verification uses the .NET BCL
/// (<see cref="ECDsa"/>) — no third-party crypto dependency on the trusted path.</para>
/// </summary>
public static class UpdateSignature
{
    /// <summary>Versioned tag prefixing the signed payload. Bumping it (e.g. <c>.v2</c>) lets us change
    /// the canonical format without old signatures validating under the new rules.</summary>
    public const string SchemeTag = "fullobby-update.v1";

    /// <summary>
    /// Trusted release-signing public key(s), each a base64-encoded <b>SubjectPublicKeyInfo</b> (DER)
    /// for an ECDSA P-256 key — i.e. the output of
    /// <c>openssl pkey -pubin -in pub.pem -outform DER | base64 -w0</c>.
    ///
    /// <para>An array so a key can be <b>rotated</b>: add the new key, ship a client that trusts both,
    /// then retire the old one in a later release. A signature is accepted if <em>any</em> listed key
    /// validates it.</para>
    ///
    /// <para>The production P-256 key below is real (private half held offline on the air-gapped
    /// signing device). If every entry were ever a placeholder/malformed, verification would still
    /// fail closed — updates are refused rather than accepted unverified. The maintainer-only
    /// key-generation/signing runbook lives in the private <c>fullobby-api</c> repo
    /// (<c>docs/RELEASE-SIGNING.md</c>).</para>
    /// </summary>
    private static readonly string[] TrustedSigningKeysBase64 =
    {
        // Production release-signing key. P-256 SPKI; private half held offline (air-gapped Pi).
        // Generated 2026-06 — rotation runbook in the private fullobby-api repo (add new key here first).
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBhNmbkxuvyMSqmXai72skJaAxc51FZTTGssUWB7F4ZCjPPY1TrdgXKFw/n0tE2qkqB3lD7krwAmOSXcV0DqpQw==",
    };

    /// <summary>
    /// The exact bytes the offline signer signs and the client re-derives and verifies:
    /// <c>fullobby-update.v1\nversion=&lt;version&gt;\nsha256=&lt;lowercase-hex&gt;</c> — UTF-8,
    /// LF separators, <b>no trailing newline</b>. Must byte-for-byte match the offline signer
    /// (<c>sign-release.sh</c> in the <c>fullobby-api</c> repo).
    /// </summary>
    public static string BuildSignedPayload(string version, string sha256) =>
        $"{SchemeTag}\nversion={version}\nsha256={sha256.ToLowerInvariant()}";

    /// <summary>
    /// Verify a release signature against the embedded trusted key(s). Returns <c>null</c> when a
    /// trusted key validates the signature over the canonical <paramref name="version"/>/
    /// <paramref name="sha256"/> payload; otherwise a human-readable error. A missing/blank signature
    /// is an error — signing is <b>mandatory</b>.
    /// </summary>
    public static string? VerifySignature(string version, string sha256, string? signatureBase64) =>
        VerifySignature(version, sha256, signatureBase64, TrustedSigningKeysBase64);

    /// <summary>
    /// Core verification against an explicit trusted-key set. Exposed (internal) so tests can supply
    /// an ephemeral key instead of the embedded production one.
    /// </summary>
    internal static string? VerifySignature(
        string version, string sha256, string? signatureBase64, IReadOnlyList<string> trustedKeysBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return "Update has no signature — refusing (release signing is mandatory)";
        }

        if (!TryFromBase64(signatureBase64, out var signature))
        {
            return "Update signature is not valid base64";
        }

        var payload = Encoding.UTF8.GetBytes(BuildSignedPayload(version, sha256));

        var trusted = 0;
        foreach (var keyBase64 in trustedKeysBase64)
        {
            if (!TryFromBase64(keyBase64, out var spki))
            {
                continue; // placeholder / malformed key entry — skip, don't trust it
            }

            using var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            }
            catch (CryptographicException)
            {
                continue; // not an importable SPKI key — skip
            }

            trusted++;
            // DER (Rfc3279DerSequence) is what `openssl dgst -sha256 -sign` emits.
            if (ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return null;
            }
        }

        return trusted == 0
            ? "No release signing key is configured in this build — refusing update"
            : "Update signature does not match any trusted release key";
    }

    private static bool TryFromBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
