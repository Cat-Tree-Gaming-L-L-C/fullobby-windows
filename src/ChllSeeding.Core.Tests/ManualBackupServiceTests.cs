using ChllSeeding.Core.Tools;

namespace ChllSeeding.Core.Tests;

public class ManualBackupServiceTests
{
    // ── IsTimestampFolder tests ─────────────────────────────────────────────────

    [Theory]
    [InlineData("2024-01-15_08-30-00")]
    [InlineData("2099-12-31_23-59-59")]
    public void IsTimestampFolder_Valid(string name)
    {
        Assert.True(ManualBackupService.IsTimestampFolder(name));
    }

    [Theory]
    [InlineData("2024-01-15_08-30")]          // too short
    [InlineData("2024-01-15_08-30-00-extra")] // too long
    [InlineData("2024/01/15_08-30-00")]       // wrong separators
    [InlineData("2024-01-15-08-30-00")]       // wrong separator at _
    [InlineData("2024-01-15T08-30-00")]       // wrong separator at _
    [InlineData("abcd-01-15_08-30-00")]       // non-digit
    [InlineData("2024-ab-15_08-30-00")]       // non-digit
    [InlineData("competitive")]               // user-named
    [InlineData("my-backup-2024-v2")]         // user-named
    public void IsTimestampFolder_Invalid(string name)
    {
        Assert.False(ManualBackupService.IsTimestampFolder(name));
    }

    // ── IsFileUnchanged (port of is_file_unchanged_cached tests) ────────────────

    private static ManualBackupService.FileCompareInfo Info(long size, DateTimeOffset? modified) => new(size, modified);

    [Fact]
    public void Unchanged_MatchingSizeAndTime()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Info(1024, now);
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo> { ["file.ini"] = Info(1024, now) };
        Assert.True(ManualBackupService.IsFileUnchanged(source, cache, "file.ini"));
    }

    [Fact]
    public void Unchanged_SizeMismatch_False()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Info(2048, now);
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo> { ["file.ini"] = Info(1024, now) };
        Assert.False(ManualBackupService.IsFileUnchanged(source, cache, "file.ini"));
    }

    [Fact]
    public void Unchanged_WithinTolerance_True()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Info(1024, now);
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo> { ["file.ini"] = Info(1024, now.AddSeconds(-1)) };
        Assert.True(ManualBackupService.IsFileUnchanged(source, cache, "file.ini"));
    }

    [Fact]
    public void Unchanged_BeyondTolerance_False()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Info(1024, now);
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo> { ["file.ini"] = Info(1024, now.AddSeconds(-3)) };
        Assert.False(ManualBackupService.IsFileUnchanged(source, cache, "file.ini"));
    }

    [Fact]
    public void Unchanged_MissingSource_False()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo> { ["file.ini"] = Info(1024, now) };
        Assert.False(ManualBackupService.IsFileUnchanged(null, cache, "file.ini"));
    }

    [Fact]
    public void Unchanged_MissingPrev_False()
    {
        var source = Info(1024, DateTimeOffset.UtcNow);
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo>();
        Assert.False(ManualBackupService.IsFileUnchanged(source, cache, "file.ini"));
    }

    [Fact]
    public void Unchanged_MissingTimestamps_False()
    {
        var source = Info(1024, null);
        var cache = new Dictionary<string, ManualBackupService.FileCompareInfo> { ["file.ini"] = Info(1024, null) };
        Assert.False(ManualBackupService.IsFileUnchanged(source, cache, "file.ini"));
    }

    // ── ValidateUserPath ──────────

    [Fact]
    public void ValidateUserPath_TooLong_Throws()
    {
        var longPath = new string('a', 261);
        Assert.Throws<ArgumentException>(() => ManualBackupService.ValidateUserPath(longPath));
    }

    [Fact]
    public void ValidateUserPath_NullByte_Throws()
    {
        Assert.Throws<ArgumentException>(() => ManualBackupService.ValidateUserPath("C:\\temp\0evil"));
    }

    [Fact]
    public void ValidateUserPath_NonExistent_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "chll-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => ManualBackupService.ValidateUserPath(missing));
    }

    [Fact]
    public void ValidateUserPath_ExistingDir_ReturnsCanonicalWithoutTraversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chll-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Feed a path containing a ".." segment; the canonical result must resolve it away.
            var withDotDot = Path.Combine(dir, "sub", "..");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var result = ManualBackupService.ValidateUserPath(withDotDot);
            Assert.DoesNotContain("..", result.Split(Path.DirectorySeparatorChar));
            Assert.Equal(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                result.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
