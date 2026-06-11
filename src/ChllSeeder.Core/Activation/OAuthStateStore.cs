using System.Security.Cryptography;

namespace ChllSeeder.Core.Activation;

/// <summary>
/// Holds the single OAuth <c>state</c> parameter used for CSRF protection across the
/// browser round-trip. Stored when initiating OAuth and validated (single-use) on the
/// <c>chllseeder://auth/callback</c> deep link. Port of the <c>OAUTH_STATE</c> static and
/// <c>set_oauth_state</c>/<c>validate_oauth_state</c> in <c>src-rust/src/state/auth.rs</c>.
/// Thread-safe; registered as a DI singleton.
/// </summary>
public sealed class OAuthStateStore
{
    private readonly object _gate = new();
    private string? _state;

    /// <summary>Store the state before redirecting to the provider (overwrites any prior value).</summary>
    public void Set(string state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    /// <summary>
    /// Validate and consume the stored state. Returns <c>true</c> only when a state was
    /// stored and matches <paramref name="state"/>. Always consumes the stored value
    /// (single-use), so a second call — or a mismatch — returns <c>false</c>.
    /// </summary>
    public bool Validate(string state)
    {
        lock (_gate)
        {
            var stored = _state;
            _state = null;
            return stored is not null && stored == state;
        }
    }

    /// <summary>Generate a random 128-bit state value as a 32-char lowercase hex string.
    /// Mirrors the Rust <c>generate_state</c>/<c>generate_uuid</c> entropy.</summary>
    public static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
