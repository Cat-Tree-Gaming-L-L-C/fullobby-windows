using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ChllSeeder.Core.Api;

/// <summary>
/// Applies auth to outgoing requests (JWT bearer when available, else
/// <c>x-api-key</c>) and transparently refreshes the JWT on a 401, retrying the
/// request once. Concurrent 401s collapse to a single refresh. Port of the
/// auth/refresh logic in <c>api/client.rs</c> (<c>api_fetch</c> + <c>refresh_auth</c>).
/// </summary>
public sealed class AuthHandler(AuthSession session, ILogger<AuthHandler> log) : DelegatingHandler
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = session.Token;
        var apiKey = session.ApiKey;
        ApplyAuth(request, token, apiKey);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Auto-refresh on 401, only when we have a refresh token (i.e. JWT auth).
        if (response.StatusCode == HttpStatusCode.Unauthorized && session.RefreshToken is not null)
        {
            response.Dispose();
            await TryRefreshAsync(token, cancellationToken).ConfigureAwait(false);

            var retry = await CloneAsync(request).ConfigureAwait(false);
            ApplyAuth(retry, session.Token, session.ApiKey);
            return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static void ApplyAuth(HttpRequestMessage request, string? token, string? apiKey)
    {
        request.Headers.Authorization = null;
        request.Headers.Remove("x-api-key");

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("x-api-key", apiKey);
        }
    }

    /// <summary>Refresh the JWT once. Deduplicated: if another caller already
    /// refreshed (the token changed since <paramref name="tokenBefore"/>), this
    /// returns immediately.</summary>
    private async Task TryRefreshAsync(string? tokenBefore, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.Token != tokenBefore)
            {
                return; // someone else already refreshed
            }

            var refresh = session.RefreshToken;
            if (refresh is null)
            {
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiConfig.BaseUrl}/api/auth/refresh")
            {
                Content = JsonContent.Create(new { refresh_token = refresh }),
            };
            // base.SendAsync runs the inner handlers (resilience) but not this handler.
            using var resp = await base.SendAsync(req, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Token refresh failed: {Status}", (int)resp.StatusCode);
                session.ClearTokens();
                return;
            }

            var body = await resp.Content.ReadFromJsonAsync<AuthRefreshResponse>(ApiJson.Options, ct)
                .ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.Token))
            {
                session.ClearTokens();
                return;
            }
            session.SetTokens(body.Token, body.RefreshToken);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            log.LogWarning(e, "Token refresh failed");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Buffer and clone a request so it can be re-sent after a refresh.</summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
