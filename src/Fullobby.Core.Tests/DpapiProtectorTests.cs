using Fullobby.Core.Security;

namespace Fullobby.Core.Tests;

/// <summary>Tests for the DPAPI protector. DPAPI runs against the real
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
    public void TryEncrypt_NonSensitiveOrEmpty_SucceedsUnchanged()
    {
        Assert.True(DpapiProtector.TryEncrypt("username", "alice", out var user));
        Assert.Equal("alice", user);
        Assert.True(DpapiProtector.TryEncrypt("auth_token", "", out var empty));
        Assert.Equal("", empty);
    }

    [Fact]
    public void TryEncrypt_Sensitive_ProducesDecryptableCiphertext()
    {
        Assert.True(DpapiProtector.TryEncrypt("auth_token", "jwt.value.123", out var encrypted));
        Assert.True(DpapiProtector.IsEncrypted(encrypted));
        Assert.NotEqual("jwt.value.123", encrypted);
        Assert.Equal("jwt.value.123", DpapiProtector.Decrypt(encrypted));
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
    public void Encrypt_BindsAppEntropy_NullEntropyCannotDecrypt()
    {
        // Prove app-specific entropy is actually applied: a same-user process that hands our blob
        // straight to DPAPI without the entropy (the null-entropy case) must fail to decrypt.
        var encrypted = DpapiProtector.Encrypt("bound_to_this_app");
        var ciphertext = Convert.FromBase64String(encrypted["dpapi:".Length..]);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            System.Security.Cryptography.ProtectedData.Unprotect(
                ciphertext,
                optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void Decrypt_LegacyNullEntropyBlob_StillDecrypts()
    {
        // A blob written before entropy was introduced (null entropy) must still decrypt so a
        // pre-release upgrade doesn't lose the stored token.
        const string original = "legacy_token_value";
        var legacy = "dpapi:" + Convert.ToBase64String(
            System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(original),
                optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        Assert.Equal(original, DpapiProtector.Decrypt(legacy));
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
