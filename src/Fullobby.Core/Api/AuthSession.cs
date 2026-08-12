using Fullobby.Core.Config;

namespace Fullobby.Core.Api;

/// <summary>
/// Holds the current auth credentials — JWT + refresh token, or a programmatic
/// API key — and persists them (DPAPI-encrypted) through <see cref="ConfigService"/>.
/// Single source of truth for the session's auth/token state. Thread-safe; DI singleton.
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
        // Flush past the debounce: the second write above always lands inside the
        // 500 ms window, so without this the on-disk refresh token stays the
        // PREVIOUS (already-consumed) one until some later write — a crash or kill
        // then restores a dead token and costs the whole session.
        _config.FlushPendingSaves();
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
        // Removal is throttled like any other write, so flush: credentials must be *gone* from
        // disk the moment we clear them. A kill inside the debounce window would otherwise leave
        // a signed-out user's refresh token sitting in config.json.
        _config.FlushPendingSaves();
    }

    public void SetApiKey(string key)
    {
        lock (_gate)
        {
            _apiKey = key;
        }
        _config.SetString("api_key", key);
        // Credentials must be durable immediately (see SetTokens) — an unflushed
        // rotated API key is unrecoverable after a crash.
        _config.FlushPendingSaves();
    }

    public void ClearApiKey()
    {
        lock (_gate)
        {
            _apiKey = null;
        }
        _config.Remove("api_key");
        _config.FlushPendingSaves(); // durable immediately, same reason as ClearTokens
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
