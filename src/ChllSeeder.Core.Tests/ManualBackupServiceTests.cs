using ChllSeeder.Core.Tools;

namespace ChllSeeder.Core.Tests;

public class ManualBackupServiceTests
{
    // ── IsTimestampFolder (port of the Rust is_timestamp_folder tests) ──────────

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
}
