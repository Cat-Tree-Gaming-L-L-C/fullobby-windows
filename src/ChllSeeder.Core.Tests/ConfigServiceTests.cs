using ChllSeeder.Core.Config;
using ChllSeeder.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChllSeeder.Core.Tests;

public class ConfigValidationTests
{
    [Theory]
    [InlineData("player_name")]
    [InlineData("session_id")]
    [InlineData("api_key")]
    [InlineData("a")]
    [InlineData("1")]
    [InlineData("key-with-dashes")]
    [InlineData("key_with_underscores")]
    [InlineData("key.with.dots")]
    [InlineData("key/with/slashes")]
    [InlineData("키")] // Korean
    [InlineData("clé")] // French
    public void ValidKeys(string key) => Assert.True(ConfigService.IsValidKey(key));

    [Fact]
    public void ValidKey_MaxLength() => Assert.True(ConfigService.IsValidKey(new string('a', 255)));

    [Fact]
    public void InvalidKey_Empty() => Assert.False(ConfigService.IsValidKey(""));

    [Fact]
    public void InvalidKey_TooLong() => Assert.False(ConfigService.IsValidKey(new string('a', 256)));

    [Theory]
    [InlineData("test\0key")]
    [InlineData("\0")]
    [InlineData("key\0")]
    public void InvalidKey_NullByte(string key) => Assert.False(ConfigService.IsValidKey(key));

    [Theory]
    [InlineData("Alice")]
    [InlineData("bob_smith")]
    [InlineData("John Doe")]
    [InlineData("user.name")]
    [InlineData("user-name")]
    public void SafeUsername_Normal(string name) => Assert.True(ConfigService.IsSafeUsername(name));

    [Fact]
    public void SafeUsername_RejectsEmpty() => Assert.False(ConfigService.IsSafeUsername(""));

    [Fact]
    public void SafeUsername_LengthBoundary()
    {
        Assert.True(ConfigService.IsSafeUsername(new string('a', 104)));
        Assert.False(ConfigService.IsSafeUsername(new string('a', 105)));
    }

    [Theory]
    [InlineData("user;whoami")]
    [InlineData("user&calc")]
    [InlineData("user|dir")]
    [InlineData("user$(cmd)")]
    [InlineData("user`cmd`")]
    [InlineData("user\nname")]
    [InlineData("user\0name")]
    [InlineData("../admin")]
    public void SafeUsername_RejectsSpecialChars(string name) => Assert.False(ConfigService.IsSafeUsername(name));
}

public class AtomicFileTests
{
    [Fact]
    public void WriteAllBytes_CreatesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chll_test_write_safe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "test_config.json");
            AtomicFile.WriteAllBytes(target, "{\"key\": \"value\"}"u8.ToArray());

            Assert.Equal("{\"key\": \"value\"}", File.ReadAllText(target));
            Assert.False(File.Exists(Path.Combine(dir, $"config.json.tmp_{Environment.ProcessId}")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteAllBytes_OverwritesExisting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chll_test_write_safe_ow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "test_config.json");
            File.WriteAllText(target, "old content");
            AtomicFile.WriteAllBytes(target, "new content"u8.ToArray());
            Assert.Equal("new content", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ConfigServiceRoundtripTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;

    public ConfigServiceRoundtripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "chll_test_config_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void SetGet_String_Roundtrip()
    {
        _config.SetString("display_name", "Bravo");
        Assert.Equal("Bravo", _config.GetString("display_name"));
    }

    [Fact]
    public void Get_Missing_ReturnsNull() => Assert.Null(_config.GetString("nope"));

    [Fact]
    public void GetBool_ParsesStringFlags()
    {
        _config.SetString("efficiency_mode", "true");
        Assert.True(_config.GetBool("efficiency_mode"));
        _config.SetString("efficiency_mode", "false");
        Assert.False(_config.GetBool("efficiency_mode"));
        Assert.True(_config.GetBool("absent", fallback: true));
    }

    [Fact]
    public void SensitiveKey_EncryptedOnDisk_DecryptedOnRead()
    {
        _config.SetString("auth_token", "jwt.value.123");
        _config.FlushPendingSaves();

        // On disk the value is DPAPI ciphertext, never plaintext.
        var onDisk = File.ReadAllText(Path.Combine(_dir, "config.json"));
        Assert.DoesNotContain("jwt.value.123", onDisk);
        Assert.Contains("dpapi:", onDisk);

        // A fresh service reading the same file decrypts transparently.
        var reopened = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
        Assert.Equal("jwt.value.123", reopened.GetString("auth_token"));
    }

    [Fact]
    public void InvalidKey_Throws() =>
        Assert.Throws<ArgumentException>(() => _config.SetString("", "x"));

    [Fact]
    public void Persists_AcrossInstances()
    {
        _config.SetString("region", "eu");
        _config.FlushPendingSaves();
        var reopened = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
        Assert.Equal("eu", reopened.GetString("region"));
    }
}
