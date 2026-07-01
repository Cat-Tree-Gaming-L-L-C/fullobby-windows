using System.Security.Cryptography;

namespace ChllSeeding.Core.Activation;

/// <summary>
/// Holds the single OAuth <c>state</c> parameter used for CSRF protection across the
/// browser round-trip. Stored when initiating OAuth and validated (single-use) on the
/// <c>chllseeding://auth/callback</c> deep link.
///
/// Provider linking is CSRF-protected server-side (link-init mints a single-use, HMAC-signed,
/// user-bound state that the OAuth callback atomically consumes), and unlike the login callback the
/// <c>auth/link-callback</c> deep link carries no token or state — the sensitive action already
/// happened server-side. This tracks a single-use "pending link" marker, set when the client
/// initiates a link and consumed on the <c>chllseeding://auth/link-callback</c> deep link, purely as
/// client-side defense-in-depth so a forged callback the client never initiated is ignored (see
/// docs/ARCHITECTURE.md).
/// Thread-safe; registered as a DI singleton.
/// </summary>
public sealed class OAuthStateStore
{
    private readonly object _gate = new();
    private string? _state;
    private string? _pendingLinkProvider;

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

    /// <summary>Record that a provider link was initiated, before opening the browser (overwrites any
    /// prior pending link — only the most recent link flow can complete).</summary>
    public void SetPendingLink(string provider)
    {
        lock (_gate)
        {
            _pendingLinkProvider = provider;
        }
    }

    /// <summary>Validate and consume the pending link. Returns <c>true</c> only when a link for
    /// <paramref name="provider"/> was initiated by this client. Always consumes the marker
    /// (single-use), so a replayed or forged link callback returns <c>false</c>.</summary>
    public bool ConsumePendingLink(string provider)
    {
        lock (_gate)
        {
            var pending = _pendingLinkProvider;
            _pendingLinkProvider = null;
            return pending is not null && pending == provider;
        }
    }

    /// <summary>Generate a random 128-bit state value as a 32-char lowercase hex string.</summary>
    public static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
