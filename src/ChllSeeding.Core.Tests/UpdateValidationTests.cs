using System.Text;
using ChllSeeding.Core.Update;

namespace ChllSeeding.Core.Tests;

/// <summary>
/// Port of the <c>#[cfg(test)]</c> block in <c>src-rust/src/platform/updater.rs</c> — the
/// security-critical validation + sanitization helpers, plus the version-availability check.
/// </summary>
public class UpdateValidationTests
{
    private static readonly string[] TrustedHosts =
        { "seeding.comp-hll.org", "github.com", "objects.githubusercontent.com" };

    // ── ValidateInstallerExtension ──────────────────────────────────

    [Theory]
    [InlineData("exe")]
    [InlineData("msi")]
    [InlineData("EXE")]
    [InlineData("MSI")]
    [InlineData("Exe")]
    public void Extension_Allowed(string ext) =>
        Assert.Null(UpdateValidation.ValidateInstallerExtension(ext));

    [Theory]
    [InlineData("bat")]
    [InlineData("ps1")]
    [InlineData("cmd")]
    [InlineData("sh")]
    [InlineData("")]
    public void Extension_Rejected(string ext) =>
        Assert.NotNull(UpdateValidation.ValidateInstallerExtension(ext));

    // ── SanitizeInstallerFilename ───────────────────────────────────

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
    public void Sanitize_DotsAndSlashesOnly_FallsBack() =>
        Assert.Equal("chll-seeding-update.exe", UpdateValidation.SanitizeInstallerFilename("../\\"));

    [Fact]
    public void Sanitize_Empty_FallsBack() =>
        Assert.Equal("chll-seeding-update.exe", UpdateValidation.SanitizeInstallerFilename(""));

    // ── VerifySha256 ────────────────────────────────────────────────

    [Fact]
    public void Sha256_Valid()
    {
        var data = Encoding.ASCII.GetBytes("hello world");
        const string expected = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";
        Assert.Null(UpdateValidation.VerifySha256(data, expected));
    }

    [Fact]
    public void Sha256_Mismatch()
    {
        var data = Encoding.ASCII.GetBytes("hello world");
        const string wrong = "0000000000000000000000000000000000000000000000000000000000000000";
        var error = UpdateValidation.VerifySha256(data, wrong);
        Assert.NotNull(error);
        Assert.Contains("Checksum mismatch", error);
    }

    [Fact]
    public void Sha256_WrongLength()
    {
        var data = Encoding.ASCII.GetBytes("hello");
        var error = UpdateValidation.VerifySha256(data, "abc123");
        Assert.NotNull(error);
        Assert.Contains("Invalid SHA-256 hash length", error);
    }

    [Theory]
    [InlineData("B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9")]
    [InlineData("B94d27b9934D3e08A52e52d7DA7dabfAC484efe37A5380ee9088F7ace2EFCDE9")]
    public void Sha256_CaseInsensitive(string hash)
    {
        var data = Encoding.ASCII.GetBytes("hello world");
        Assert.Null(UpdateValidation.VerifySha256(data, hash));
    }

    [Fact]
    public void Sha256_EmptyInput()
    {
        var data = Array.Empty<byte>();
        const string expected = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        Assert.Null(UpdateValidation.VerifySha256(data, expected));
    }

    // ── ValidateInstallerSize ───────────────────────────────────────

    [Theory]
    [InlineData(100L * 1024 * 1024)] // 100 MB
    [InlineData(500L * 1024 * 1024)] // exactly 500 MB
    [InlineData(0L)]
    public void Size_WithinLimit(long len) =>
        Assert.Null(UpdateValidation.ValidateInstallerSize(len));

    [Fact]
    public void Size_OverLimit()
    {
        var error = UpdateValidation.ValidateInstallerSize(500L * 1024 * 1024 + 1);
        Assert.NotNull(error);
        Assert.Contains("too large", error);
    }

    // ── ValidateDownloadUrl ─────────────────────────────────────────

    [Theory]
    [InlineData("https://seeding.comp-hll.org/releases/v1.0.exe")]
    [InlineData("https://github.com/org/repo/releases/download/v1/setup.exe")]
    [InlineData("https://objects.githubusercontent.com/path/to/file")]
    public void DownloadUrl_TrustedDomains(string url) =>
        Assert.Null(UpdateValidation.ValidateDownloadUrl(url, TrustedHosts));

    [Fact]
    public void DownloadUrl_RejectsHttp()
    {
        var error = UpdateValidation.ValidateDownloadUrl("http://seeding.comp-hll.org/file.exe", TrustedHosts);
        Assert.NotNull(error);
        Assert.Contains("HTTPS", error);
    }

    [Fact]
    public void DownloadUrl_RejectsUntrustedDomain()
    {
        var error = UpdateValidation.ValidateDownloadUrl("https://evil.com/malware.exe", TrustedHosts);
        Assert.NotNull(error);
        Assert.Contains("not in the trusted domain list", error);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void DownloadUrl_RejectsInvalid(string url) =>
        Assert.NotNull(UpdateValidation.ValidateDownloadUrl(url, TrustedHosts));

    // ── IsUpdateAvailable ───────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]   // newer
    [InlineData("1.0.0", "0.9.0", true)]   // server decides ordering — any difference is "available"
    [InlineData("1.0.0", "1.0.0", false)]  // same
    [InlineData("1.0.0", "", false)]       // empty latest → no update
    public void IsUpdateAvailable_Cases(string current, string latest, bool expected) =>
        Assert.Equal(expected, UpdateValidation.IsUpdateAvailable(current, latest));
}
