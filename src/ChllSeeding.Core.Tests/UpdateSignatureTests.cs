using System.Security.Cryptography;
using System.Text;
using ChllSeeding.Core.Update;

namespace ChllSeeding.Core.Tests;

/// <summary>
/// Tests for the pinned-key release-signature check. Uses an ephemeral P-256 key (via the internal
/// trusted-keys overload) so the tests don't depend on the embedded production key, and signs with the
/// exact format the offline signer uses (ECDSA / SHA-256, DER signature).
/// </summary>
public class UpdateSignatureTests
{
    private const string Version = "1.2.3";
    private const string Sha256 = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";

    /// <summary>Mirror what the offline signer (<c>sign-release.sh</c>) does: sign the canonical payload bytes with
    /// ECDSA/SHA-256, DER-encoded — and return (base64 signature, base64 SPKI public key).</summary>
    private static (string Signature, string PublicKeyBase64) Sign(string version, string sha256, ECDsa key)
    {
        var payload = Encoding.UTF8.GetBytes(UpdateSignature.BuildSignedPayload(version, sha256));
        var sig = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (Convert.ToBase64String(sig), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Verify_ValidSignature_Passes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, pub) = Sign(Version, Sha256, key);

        Assert.Null(UpdateSignature.VerifySignature(Version, Sha256, sig, new[] { pub }));
    }

    [Fact]
    public void Verify_MultipleTrustedKeys_AcceptsAny()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, pub) = Sign(Version, Sha256, signingKey);
        var otherPub = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo());

        // Signed by the second of the two trusted keys (rotation scenario).
        Assert.Null(UpdateSignature.VerifySignature(Version, Sha256, sig, new[] { otherPub, pub }));
    }

    [Fact]
    public void Verify_TamperedVersion_Fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, pub) = Sign(Version, Sha256, key);

        var error = UpdateSignature.VerifySignature("1.2.4", Sha256, sig, new[] { pub });
        Assert.NotNull(error);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void Verify_TamperedSha256_Fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, pub) = Sign(Version, Sha256, key);

        var tampered = Sha256[..63] + "0"; // flip the last hex char
        var error = UpdateSignature.VerifySignature(Version, tampered, sig, new[] { pub });
        Assert.NotNull(error);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, pub) = Sign(Version, Sha256, key);

        // Corrupt one signature byte while keeping it valid base64 / DER-parseable length.
        var raw = Convert.FromBase64String(sig);
        raw[^1] ^= 0xFF;
        var bad = Convert.ToBase64String(raw);

        Assert.NotNull(UpdateSignature.VerifySignature(Version, Sha256, bad, new[] { pub }));
    }

    [Fact]
    public void Verify_WrongKey_Fails()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, _) = Sign(Version, Sha256, signingKey);
        var wrongPub = Convert.ToBase64String(wrongKey.ExportSubjectPublicKeyInfo());

        var error = UpdateSignature.VerifySignature(Version, Sha256, sig, new[] { wrongPub });
        Assert.NotNull(error);
        Assert.Contains("does not match", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_MissingSignature_Fails(string? sig)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var error = UpdateSignature.VerifySignature(Version, Sha256, sig, new[] { pub });
        Assert.NotNull(error);
        Assert.Contains("no signature", error);
    }

    [Fact]
    public void Verify_MalformedBase64Signature_FailsGracefully()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var error = UpdateSignature.VerifySignature(Version, Sha256, "not!base64!", new[] { pub });
        Assert.NotNull(error);
        Assert.Contains("not valid base64", error);
    }

    [Fact]
    public void Verify_NoTrustedKeys_FailsClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (sig, _) = Sign(Version, Sha256, key);

        // Only a placeholder / unimportable "key" present — must refuse, never accept.
        var error = UpdateSignature.VerifySignature(Version, Sha256, sig, new[] { "REPLACE_ME_placeholder" });
        Assert.NotNull(error);
        Assert.Contains("No release signing key", error);
    }

    [Fact]
    public void BuildSignedPayload_IsCanonical()
    {
        // Exact byte layout the offline signer must reproduce: tag, LF, version, LF, lowercase sha256,
        // no trailing newline.
        var payload = UpdateSignature.BuildSignedPayload("1.2.3", "ABCDEF");
        Assert.Equal("chll-seeding-update.v1\nversion=1.2.3\nsha256=abcdef", payload);
    }
}
