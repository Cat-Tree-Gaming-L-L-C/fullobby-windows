using ChllSeeding.Core.Security;

namespace ChllSeeding.Core.Tests;

/// <summary>Port of the <c>crypto.rs</c> #[test] coverage. DPAPI runs against the real
/// current-user keystore (Windows test host).</summary>
public class DpapiProtectorTests
{
    [Fact]
    public void SensitiveKeysList()
    {
        Assert.Contains("auth_token", DpapiProtector.SensitiveKeys);
        Assert.Contains("auth_refresh_token", DpapiProtector.SensitiveKeys);
        Assert.Contains("api_key", DpapiProtector.SensitiveKeys);
        Assert.DoesNotContain("username", DpapiProtector.SensitiveKeys);
        Assert.DoesNotContain("theme", DpapiProtector.SensitiveKeys);
    }

    [Fact]
    public void MaybeEncrypt_NonSensitive_Passthrough()
    {
        Assert.Equal("alice", DpapiProtector.MaybeEncrypt("username", "alice"));
        Assert.Equal("dark", DpapiProtector.MaybeEncrypt("theme", "dark"));
        Assert.Equal("Bob", DpapiProtector.MaybeEncrypt("display_name", "Bob"));
    }

    [Fact]
    public void MaybeEncrypt_EmptyValue_Passthrough()
    {
        Assert.Equal("", DpapiProtector.MaybeEncrypt("auth_token", ""));
        Assert.Equal("", DpapiProtector.MaybeEncrypt("api_key", ""));
    }

    [Fact]
    public void MaybeDecrypt_NonSensitive_Passthrough()
    {
        Assert.Equal("alice", DpapiProtector.MaybeDecrypt("username", "alice"));
        Assert.Equal("dark", DpapiProtector.MaybeDecrypt("theme", "dark"));
    }

    [Fact]
    public void MaybeDecrypt_EmptyValue_Passthrough()
    {
        Assert.Equal("", DpapiProtector.MaybeDecrypt("auth_token", ""));
        Assert.Equal("", DpapiProtector.MaybeDecrypt("api_key", ""));
    }

    [Fact]
    public void MaybeDecrypt_PlaintextMigration()
    {
        Assert.Equal("my_plain_token", DpapiProtector.MaybeDecrypt("auth_token", "my_plain_token"));
    }

    [Fact]
    public void Encrypt_ProducesDpapiPrefix()
    {
        var encrypted = DpapiProtector.Encrypt("hello world");
        Assert.StartsWith("dpapi:", encrypted);
    }

    [Fact]
    public void Encrypt_Decrypt_Roundtrip()
    {
        const string original = "super_secret_token_12345";
        var encrypted = DpapiProtector.Encrypt(original);
        Assert.Equal(original, DpapiProtector.Decrypt(encrypted));
    }

    [Fact]
    public void MaybeEncryptDecrypt_Roundtrip_Sensitive()
    {
        const string original = "jwt.token.value";
        var encrypted = DpapiProtector.MaybeEncrypt("auth_token", original);
        Assert.StartsWith("dpapi:", encrypted);
        Assert.Equal(original, DpapiProtector.MaybeDecrypt("auth_token", encrypted));
    }

    [Fact]
    public void Decrypt_MalformedBase64_Throws()
    {
        Assert.ThrowsAny<Exception>(() => DpapiProtector.Decrypt("dpapi:not-valid-base64!!!"));
    }

    [Fact]
    public void MaybeDecrypt_Malformed_ReturnsRaw()
    {
        const string raw = "dpapi:not-valid-base64!!!";
        Assert.Equal(raw, DpapiProtector.MaybeDecrypt("auth_token", raw));
    }
}
