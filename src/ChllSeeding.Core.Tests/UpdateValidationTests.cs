using ChllSeeding.Core.Update;
using Xunit;

namespace ChllSeeding.Core.Tests;

/// <summary>
/// Mirrors the <c>#[cfg(test)]</c> block in <c>src-rust/src/platform/updater.rs</c>: installer
/// extension/filename sanitisation, SHA-256 verification, size limits, and download-URL trust.
/// </summary>
public class UpdateValidationTests
{
    private static readonly string[] Trusted =
        ["seeding.comp-hll.org", "github.com", "objects.githubusercontent.com"];

    // ─── ValidateInstallerExtension ─────────────────────────────────

    [Theory]
    [InlineData("exe")]
    [InlineData("msi")]
    [InlineData("EXE")]
    [InlineData("MSI")]
    [InlineData("Exe")]
    public void Extension_Allowed(string ext) => Assert.Null(UpdateValidation.ValidateInstallerExtension(ext));

    [Theory]
    [InlineData("bat")]
    [InlineData("ps1")]
    [InlineData("cmd")]
    [InlineData("sh")]
    [InlineData("")]
    public void Extension_Rejected(string ext) => Assert.NotNull(UpdateValidation.ValidateInstallerExtension(ext));

    // ─── SanitizeInstallerFilename ──────────────────────────────────

    [Fact]
    public void Sanitize_NormalName() =>
        Assert.Equal("setup.exe", UpdateValidation.SanitizeInstallerFilename("setup.exe"));

    [Fact]
    public void Sanitize_PathTraversal() =>
        Assert.Equal("etcpasswd", UpdateValidation.SanitizeInstallerFilename("../../../etc/passwd"));

    [Fact]
    public void Sanitize_BackslashTraversal() =>
        Assert.Equal("setup.exe", UpdateValidation.SanitizeInstallerFilename(@"..\..\setup.exe"));

    [Fact]
    public void Sanitize_DotsAndSlashesOnly() =>
        Assert.Equal(UpdateValidation.DefaultInstallerName, UpdateValidation.SanitizeInstallerFilename("../\\"));

    [Fact]
    public void Sanitize_Empty() =>
        Assert.Equal(UpdateValidation.DefaultInstallerName, UpdateValidation.SanitizeInstallerFilename(""));

    // ─── VerifySha256 ───────────────────────────────────────────────

    [Fact]
    public void Sha256_Valid()
    {
        var data = "hello world"u8.ToArray();
        Assert.Null(UpdateValidation.VerifySha256(
            data, "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9"));
    }

    [Fact]
    public void Sha256_Mismatch()
    {
        var data = "hello world"u8.ToArray();
        var result = UpdateValidation.VerifySha256(
            data, "0000000000000000000000000000000000000000000000000000000000000000");
        Assert.NotNull(result);
        Assert.Contains("Checksum mismatch", result);
    }

    [Fact]
    public void Sha256_WrongLength()
    {
        var result = UpdateValidation.VerifySha256("hello"u8.ToArray(), "abc123");
        Assert.NotNull(result);
        Assert.Contains("Invalid SHA-256 hash length", result);
    }

    [Theory]
    [InlineData("B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9")]
    [InlineData("B94d27b9934D3e08A52e52d7DA7dabfAC484efe37A5380ee9088F7ace2EFCDE9")]
    public void Sha256_CaseInsensitive(string hash) =>
        Assert.Null(UpdateValidation.VerifySha256("hello world"u8.ToArray(), hash));

    [Fact]
    public void Sha256_EmptyInput() =>
        Assert.Null(UpdateValidation.VerifySha256(
            [], "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

    // ─── ValidateInstallerSize ──────────────────────────────────────

    [Fact]
    public void Size_UnderLimit() => Assert.Null(UpdateValidation.ValidateInstallerSize(100L * 1024 * 1024));

    [Fact]
    public void Size_AtLimit() => Assert.Null(UpdateValidation.ValidateInstallerSize(500L * 1024 * 1024));

    [Fact]
    public void Size_OverLimit()
    {
        var result = UpdateValidation.ValidateInstallerSize(500L * 1024 * 1024 + 1);
        Assert.NotNull(result);
        Assert.Contains("too large", result);
    }

    [Fact]
    public void Size_Zero() => Assert.Null(UpdateValidation.ValidateInstallerSize(0));

    // ─── ValidateDownloadUrl ────────────────────────────────────────

    [Theory]
    [InlineData("https://seeding.comp-hll.org/releases/v1.0.exe")]
    [InlineData("https://github.com/org/repo/releases/download/v1/setup.exe")]
    [InlineData("https://objects.githubusercontent.com/path/to/file")]
    public void DownloadUrl_TrustedDomains(string url) =>
        Assert.Null(UpdateValidation.ValidateDownloadUrl(url, Trusted));

    [Fact]
    public void DownloadUrl_RejectsHttp()
    {
        var result = UpdateValidation.ValidateDownloadUrl("http://seeding.comp-hll.org/file.exe", Trusted);
        Assert.NotNull(result);
        Assert.Contains("HTTPS", result);
    }

    [Fact]
    public void DownloadUrl_RejectsUntrustedDomain()
    {
        var result = UpdateValidation.ValidateDownloadUrl("https://evil.com/malware.exe", Trusted);
        Assert.NotNull(result);
        Assert.Contains("not in the trusted domain list", result);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void DownloadUrl_RejectsInvalidUrl(string url) =>
        Assert.NotNull(UpdateValidation.ValidateDownloadUrl(url, Trusted));

    // ─── IsUpdateAvailable ──────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "", false)]
    public void IsUpdateAvailable_Cases(string current, string latest, bool expected) =>
        Assert.Equal(expected, UpdateValidation.IsUpdateAvailable(current, latest));
}
