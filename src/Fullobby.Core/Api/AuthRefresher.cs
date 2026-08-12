using System.Net;
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
                // Only a definitive rejection (401/403) ends the session. A 429, a 5xx,
                // or a proxy error page is the backend having a moment — wiping the
                // 14-day refresh token here turned every such blip into a forced
                // re-login (and, with no credentials left on disk, a re-armed
                // onboarding overlay on the next launch).
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    log.LogWarning("Token refresh rejected ({Status}); clearing session", (int)resp.StatusCode);
                    session.ClearTokens();
                }
                else
                {
                    log.LogWarning(
                        "Token refresh failed transiently ({Status}); keeping tokens for retry",
                        (int)resp.StatusCode);
                }
                return false;
            }

            var body = await resp.Content.ReadFromJsonAsync<AuthRefreshResponse>(ApiJson.Options, ct)
                .ConfigureAwait(false);
            if (body is null || string.IsNullOrEmpty(body.Token))
            {
                // A 2xx with an unusable body is malformed transport, not a rejection —
                // keep the tokens; the server's short replay grace lets the next attempt
                // recover the rotation.
                log.LogWarning("Token refresh returned an unusable body; keeping tokens");
                return false;
            }
            // Never overwrite a real refresh token with an empty field from a
            // partial/deserialization-defaulted body.
            session.SetTokens(body.Token, string.IsNullOrEmpty(body.RefreshToken) ? refresh : body.RefreshToken);
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
