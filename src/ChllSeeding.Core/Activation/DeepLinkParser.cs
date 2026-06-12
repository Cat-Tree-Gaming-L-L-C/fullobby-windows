namespace ChllSeeding.Core.Activation;

/// <summary>
/// Parses <c>chllseeding://</c> URLs into <see cref="DeepLinkAction"/>s.
/// Port of <c>src-rust/src/platform/deep_link.rs</c> (rebranded scheme).
/// Expected format: <c>chllseeding://auth/callback?token=...&amp;refresh_token=...</c>
/// </summary>
public static class DeepLinkParser
{
    /// <summary>Valid provider names for link callbacks.</summary>
    private static readonly string[] ValidProviders = ["steam", "discord"];

    /// <summary>Maximum length for any single deep-link parameter key or value.</summary>
    private const int MaxParamLength = 4096;

    /// <summary>Maximum number of query parameters accepted in a deep link.</summary>
    private const int MaxParams = 16;

    /// <summary>
    /// Parse a deep-link URL. Returns <c>null</c> when the URL does not use the
    /// <c>chllseeding://</c> scheme; otherwise an action (possibly <see cref="DeepLinkAction.Unknown"/>).
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
                if (parameters.TryGetValue("provider", out var provider)
                    && parameters.TryGetValue("provider_id", out var providerId)
                    && provider.Length > 0
                    && providerId.Length > 0)
                {
                    if (!ValidProviders.Contains(provider))
                    {
                        // Link callback with unknown provider
                        return new DeepLinkAction.Unknown(url);
                    }
                    return new DeepLinkAction.LinkCallback(provider, providerId);
                }
                // Link callback missing provider or provider_id
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
