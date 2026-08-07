using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Api;

/// <summary>
/// Refreshes the JWT once, deduplicating concurrent callers: if another caller
/// already refreshed (the token changed since the one observed before the 401),
/// this returns immediately. Extracted from <c>AuthHandler</c> so both the typed
/// client (via <see cref="AuthHandler"/>) and the SSE stream (which uses a separate
/// handler-less client) share a single global refresh gate.
/// </summary>
/// <remarks>Uses the named <c>"auth"</c> client (resilience only, no <see cref="AuthHandler"/>)
/// so a refresh never recurses back through auth and never rides the infinite-timeout SSE client.</remarks>
public sealed class AuthRefresher(IHttpClientFactory httpFactory, AuthSession session, ILogger<AuthRefresher> log)
{
    /// <summary>Named client used for the refresh call. See <c>ServiceCollectionExtensions</c>.</summary>
    public const string ClientName = "auth";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Refresh the JWT once. <paramref name="tokenBefore"/> is the token value the caller
    /// saw on the request that got the 401; if it has since changed, another refresh already ran.
    /// Returns <c>true</c> when a usable token is in place afterwards (freshly refreshed, or already
    /// refreshed by a concurrent caller), <c>false</c> when the refresh failed and tokens were cleared
    /// — the caller should then surface the original 401 rather than retry. Port of <c>refresh_auth</c>.</summary>
    public async Task<bool> RefreshAsync(string? tokenBefore, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.Token != tokenBefore)
            {
                return true; // someone else already refreshed — the current token is usable
            }

            var refresh = session.RefreshToken;
            if (refresh is null)
            {
                return false;
            }

            var http = httpFactory.CreateClient(ClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiConfig.BaseUrl}/api/auth/refresh")
            {
                Content = JsonContent.Create(new { refresh_token = refresh }),
            };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Token refresh failed: {Status}", (int)resp.StatusCode);
                session.ClearTokens();
                return false;
            }

            var body = await resp.Content.ReadFromJsonAsync<AuthRefreshResponse>(ApiJson.Options, ct)
                .ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.Token))
            {
                session.ClearTokens();
                return false;
            }
            session.SetTokens(body.Token, body.RefreshToken);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            log.LogWarning(e, "Token refresh failed");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
