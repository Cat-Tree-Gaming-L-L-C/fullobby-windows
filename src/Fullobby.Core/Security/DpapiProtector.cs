using System.Security.Cryptography;
using System.Text;

namespace Fullobby.Core.Security;

/// <summary>
/// Encrypts sensitive config values with Windows DPAPI (current-user scope).
/// Preserves the on-disk <c>dpapi:&lt;base64&gt;</c> format so existing encrypted values
/// keep working.
/// </summary>
public static class DpapiProtector
{
    /// <summary>Keys whose values are DPAPI-encrypted before being written to config.</summary>
    public static readonly string[] SensitiveKeys = ["auth_token", "auth_refresh_token", "api_key"];

    /// <summary>Prefix marking an encrypted value, distinguishing it from plaintext.</summary>
    private const string EncryptedPrefix = "dpapi:";

    /// <summary>
    /// App-specific secondary entropy mixed into every DPAPI operation. DPAPI's current-user scope
    /// already stops other users decrypting the blob; this additionally binds the ciphertext to this
    /// application, so another process running as the same user cannot decrypt our tokens by simply
    /// handing the blob back to <c>CryptUnprotectData</c>. It is not a secret key (it ships in the
    /// binary) — it is a domain separator, exactly the use DPAPI's optionalEntropy is designed for.
    /// </summary>
    private static readonly byte[] AppEntropy =
        Encoding.UTF8.GetBytes("com.fullobby.app/dpapi/v1");

    /// <summary>
    /// Encrypt a plaintext string with DPAPI (current-user scope) plus app-specific entropy.
    /// Returns a base64 ciphertext prefixed with <c>dpapi:</c>.
    /// </summary>
    public static string Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, AppEntropy, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypt a DPAPI value. A value without the <c>dpapi:</c> prefix is returned
    /// as-is (plaintext passthrough for migration from pre-encryption versions).
    /// Blobs written before app-specific entropy was introduced (null entropy) are still accepted as a
    /// fallback; they get re-encrypted with entropy on the next save.
    /// Throws <see cref="CryptographicException"/> or <see cref="FormatException"/> on failure.
    /// </summary>
    public static string Decrypt(string value)
    {
        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value; // plaintext passthrough
        }

        var ciphertext = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
        byte[] decrypted;
        try
        {
            decrypted = ProtectedData.Unprotect(ciphertext, AppEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Legacy blob written with null entropy — decrypt so we don't lose the token; the next
            // save re-encrypts it with AppEntropy. Any failure here propagates as before.
            decrypted = ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>True if <paramref name="key"/> holds a secret that must be encrypted at rest.</summary>
    public static bool IsSensitive(string key) => Array.IndexOf(SensitiveKeys, key) >= 0;

    /// <summary>True if <paramref name="value"/> is already a DPAPI ciphertext.</summary>
    public static bool IsEncrypted(string value) => value.StartsWith(EncryptedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypt a sensitive value, reporting whether protection actually succeeded.
    /// Returns <c>true</c> with the ciphertext when the value is protected (or needs no
    /// protection); <c>false</c> with the value untouched when DPAPI is unavailable, so the caller
    /// can decline to persist it rather than silently writing a token to disk in the clear.
    /// </summary>
    public static bool TryEncrypt(string key, string value, out string result)
    {
        if (!IsSensitive(key) || value.Length == 0)
        {
            result = value;
            return true;
        }
        try
        {
            result = Encrypt(value);
            return true;
        }
        catch (CryptographicException)
        {
            result = value;
            return false;
        }
    }

    /// <summary>Encrypt when the key is sensitive and the value is non-empty; otherwise pass through.
    /// On a DPAPI failure the value is returned unencrypted — callers that persist the result must
    /// use <see cref="TryEncrypt"/> instead, so an unprotected secret is never written to disk.</summary>
    public static string MaybeEncrypt(string key, string value)
    {
        if (!IsSensitive(key) || value.Length == 0)
        {
            return value;
        }
        try
        {
            return Encrypt(value);
        }
        catch (CryptographicException)
        {
            return value; // store plaintext rather than lose the value
        }
    }

    /// <summary>
    /// Decrypt when the key is sensitive and the value is non-empty; on failure return
    /// the raw value (never throws).
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
