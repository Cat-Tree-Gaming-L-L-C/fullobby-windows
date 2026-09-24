namespace Fullobby.Core.Activation;

/// <summary>
/// Parses <c>fullobby://</c> URLs into <see cref="DeepLinkAction"/>s.
/// Expected formats: <c>fullobby://auth/callback?token=...&amp;refresh_token=...</c> (and the
/// other auth callbacks), and <c>fullobby://invite?token=...</c>.
/// </summary>
public static class DeepLinkParser
{
    /// <summary>Valid provider names for link callbacks (matches the API's supported OAuth providers).</summary>
    private static readonly string[] ValidProviders = ["steam", "discord", "epic", "xbox"];

    /// <summary>Maximum length for any single deep-link parameter key or value.</summary>
    private const int MaxParamLength = 4096;

    /// <summary>Maximum number of query parameters accepted in a deep link.</summary>
    private const int MaxParams = 16;

    /// <summary>
    /// Parse a deep-link URL. Returns <c>null</c> when the URL does not use the
    /// <c>fullobby://</c> scheme; otherwise an action (possibly <see cref="DeepLinkAction.Unknown"/>).
    /// </summary>
    public static DeepLinkAction? Parse(string url)
    {
        url = url.Trim();
        if (!url.StartsWith(Branding.ProtocolPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var afterScheme = url[Branding.ProtocolPrefix.Length..];

        // Split path and query
        string path, query;
        var queryIndex = afterScheme.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = afterScheme[..queryIndex];
            query = afterScheme[(queryIndex + 1)..];
        }
        else
        {
            path = afterScheme;
            query = string.Empty;
        }

        var parameters = ParseQuery(query);

        switch (path)
        {
            case "auth/callback":
            {
                parameters.TryGetValue("state", out var state);
                if (parameters.TryGetValue("token", out var token)
                    && parameters.TryGetValue("refresh_token", out var refreshToken)
                    && token.Length > 0
                    && refreshToken.Length > 0)
                {
                    return new DeepLinkAction.AuthCallback(state, token, refreshToken);
                }
                // Auth callback missing token or refresh_token
                return new DeepLinkAction.Unknown(url);
            }
            case "auth/link-callback":
            {
                if (!parameters.TryGetValue("provider", out var provider)
                    || provider.Length == 0
                    || !ValidProviders.Contains(provider))
                {
                    // Link callback missing or unknown provider
                    return new DeepLinkAction.Unknown(url);
                }
                // A fresh link arrives staged (single-use hex code to confirm at
                // POST /api/auth/link-confirm); an already-linked identity arrives
                // as the legacy committed form (provider_id + linked=true).
                if (parameters.TryGetValue("staged", out var staged)
                    && staged.Length > 0
                    && staged.All(char.IsAsciiHexDigit))
                {
                    return new DeepLinkAction.LinkCallback(provider, null, staged);
                }
                if (parameters.TryGetValue("provider_id", out var providerId)
                    && providerId.Length > 0)
                {
                    return new DeepLinkAction.LinkCallback(provider, providerId, null);
                }
                // Link callback with neither a staged code nor a provider_id
                return new DeepLinkAction.Unknown(url);
            }
            case "auth/register-callback":
            {
                parameters.TryGetValue("state", out var state);
                if (parameters.TryGetValue("token", out var token) && token.Length > 0)
                {
                    return new DeepLinkAction.RegisterCallback(state, token);
                }
                // Register callback missing token
                return new DeepLinkAction.Unknown(url);
            }
            // A host-only URI can come back from the shell normalized with a trailing slash.
            case "invite" or "invite/":
            {
                if (parameters.TryGetValue("token", out var raw)
                    && Api.InviteLink.ParseToken(raw) is { } inviteToken)
                {
                    return new DeepLinkAction.Invite(inviteToken);
                }
                // Invite missing or malformed token
                return new DeepLinkAction.Unknown(url);
            }
            default:
                return new DeepLinkAction.Unknown(url);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length == 0)
        {
            return parameters;
        }

        var accepted = 0;
        foreach (var pair in query.Split('&'))
        {
            if (accepted >= MaxParams)
            {
                break;
            }

            var eqIndex = pair.IndexOf('=');
            var rawKey = eqIndex >= 0 ? pair[..eqIndex] : pair;
            var rawValue = eqIndex >= 0 ? pair[(eqIndex + 1)..] : string.Empty;

            string key, value;
            try
            {
                key = Uri.UnescapeDataString(rawKey);
                value = Uri.UnescapeDataString(rawValue);
            }
            catch (UriFormatException)
            {
                continue; // skip malformed percent-encoding
            }

            if (key.Length is 0 or > MaxParamLength || value.Length > MaxParamLength)
            {
                continue;
            }

            parameters[key] = value;
            accepted++;
        }

        return parameters;
    }
}
