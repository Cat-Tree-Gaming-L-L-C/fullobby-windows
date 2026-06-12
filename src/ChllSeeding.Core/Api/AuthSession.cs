using ChllSeeding.Core.Config;

namespace ChllSeeding.Core.Api;

/// <summary>
/// Holds the current auth credentials — JWT + refresh token, or a programmatic
/// API key — and persists them (DPAPI-encrypted) through <see cref="ConfigService"/>.
/// Replaces the global token state split across <c>api/client.rs</c> and
/// <c>backend/api_client.rs</c> in the Rust app. Thread-safe; DI singleton.
/// </summary>
public sealed class AuthSession
{
    private readonly ConfigService _config;
    private readonly object _gate = new();

    private string? _token;
    private string? _refreshToken;
    private string? _apiKey;

    public AuthSession(ConfigService config)
    {
        _config = config;
        _token = NullIfEmpty(config.GetString("auth_token"));
        _refreshToken = NullIfEmpty(config.GetString("auth_refresh_token"));
        _apiKey = NullIfEmpty(config.GetString("api_key"));
    }

    public string? Token { get { lock (_gate) { return _token; } } }
    public string? RefreshToken { get { lock (_gate) { return _refreshToken; } } }
    public string? ApiKey { get { lock (_gate) { return _apiKey; } } }

    /// <summary>True when either a JWT or an API key is available.</summary>
    public bool IsAuthenticated
    {
        get { lock (_gate) { return _token is not null || _apiKey is not null; } }
    }

    /// <summary>Set JWT + refresh token (after OAuth callback or a successful refresh).</summary>
    public void SetTokens(string token, string refresh)
    {
        lock (_gate)
        {
            _token = token;
            _refreshToken = refresh;
        }
        _config.SetString("auth_token", token);
        _config.SetString("auth_refresh_token", refresh);
    }

    /// <summary>Clear JWT + refresh token (logout / failed refresh).</summary>
    public void ClearTokens()
    {
        lock (_gate)
        {
            _token = null;
            _refreshToken = null;
        }
        _config.Remove("auth_token");
        _config.Remove("auth_refresh_token");
    }

    public void SetApiKey(string key)
    {
        lock (_gate)
        {
            _apiKey = key;
        }
        _config.SetString("api_key", key);
    }

    public void ClearApiKey()
    {
        lock (_gate)
        {
            _apiKey = null;
        }
        _config.Remove("api_key");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
