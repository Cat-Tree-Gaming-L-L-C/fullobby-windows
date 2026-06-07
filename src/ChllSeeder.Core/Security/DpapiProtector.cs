using System.Security.Cryptography;
using System.Text;

namespace ChllSeeder.Core.Security;

/// <summary>
/// Encrypts sensitive config values with Windows DPAPI (current-user scope).
/// Port of <c>src-rust/src/backend/crypto.rs</c>; preserves the on-disk
/// <c>dpapi:&lt;base64&gt;</c> format so existing encrypted values keep working.
/// </summary>
public static class DpapiProtector
{
    /// <summary>Keys whose values are DPAPI-encrypted before being written to config.</summary>
    public static readonly string[] SensitiveKeys = ["auth_token", "auth_refresh_token", "api_key"];

    /// <summary>Prefix marking an encrypted value, distinguishing it from plaintext.</summary>
    private const string EncryptedPrefix = "dpapi:";

    /// <summary>
    /// Encrypt a plaintext string with DPAPI (current-user scope).
    /// Returns a base64 ciphertext prefixed with <c>dpapi:</c>.
    /// </summary>
    public static string Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypt a DPAPI value. A value without the <c>dpapi:</c> prefix is returned
    /// as-is (plaintext passthrough for migration from pre-encryption versions).
    /// Throws <see cref="CryptographicException"/> or <see cref="FormatException"/> on failure.
    /// </summary>
    public static string Decrypt(string value)
    {
        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value; // plaintext passthrough
        }

        var ciphertext = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
        var decrypted = ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>True if <paramref name="key"/> holds a secret that must be encrypted at rest.</summary>
    public static bool IsSensitive(string key) => Array.IndexOf(SensitiveKeys, key) >= 0;

    /// <summary>True if <paramref name="value"/> is already a DPAPI ciphertext.</summary>
    public static bool IsEncrypted(string value) => value.StartsWith(EncryptedPrefix, StringComparison.Ordinal);

    /// <summary>Encrypt when the key is sensitive and the value is non-empty; otherwise pass through.</summary>
    public static string MaybeEncrypt(string key, string value)
    {
        if (!IsSensitive(key) || value.Length == 0)
        {
            return value;
        }
        return Encrypt(value);
    }

    /// <summary>
    /// Decrypt when the key is sensitive and the value is non-empty; on failure return
    /// the raw value (never throws) — mirrors the Rust <c>maybe_decrypt</c> behaviour.
    /// </summary>
    public static string MaybeDecrypt(string key, string value)
    {
        if (!IsSensitive(key) || value.Length == 0)
        {
            return value;
        }
        try
        {
            return Decrypt(value);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return value;
        }
    }
}
