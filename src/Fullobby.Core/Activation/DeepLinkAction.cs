namespace Fullobby.Core.Activation;

/// <summary>Parsed <c>fullobby://</c> deep-link actions.</summary>
public abstract record DeepLinkAction
{
    /// <summary>OAuth callback with JWT tokens (login flow).</summary>
    public sealed record AuthCallback(string? State, string Token, string RefreshToken) : DeepLinkAction;

    /// <summary>Provider link callback (linking an additional provider to an existing account).</summary>
    public sealed record LinkCallback(string Provider, string ProviderId) : DeepLinkAction;

    /// <summary>Cloudflare Turnstile challenge solved for guest registration (see
    /// <c>/register-challenge</c> on the backend).</summary>
    public sealed record RegisterCallback(string? State, string Token) : DeepLinkAction;

    /// <summary>Unknown or unsupported deep link.</summary>
    public sealed record Unknown(string Url) : DeepLinkAction;
}
