namespace ChllSeeding.Core.Activation;

/// <summary>Parsed <c>chllseeding://</c> deep-link actions.</summary>
public abstract record DeepLinkAction
{
    /// <summary>OAuth callback with JWT tokens (login flow).</summary>
    public sealed record AuthCallback(string? State, string Token, string RefreshToken) : DeepLinkAction;

    /// <summary>Provider link callback (linking an additional provider to an existing account).</summary>
    public sealed record LinkCallback(string Provider, string ProviderId) : DeepLinkAction;

    /// <summary>Unknown or unsupported deep link.</summary>
    public sealed record Unknown(string Url) : DeepLinkAction;
}
