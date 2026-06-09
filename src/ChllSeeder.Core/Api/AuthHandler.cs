using System.Net;
using Microsoft.Extensions.Logging;

namespace ChllSeeder.Core.Api;

/// <summary>
/// Applies auth to outgoing requests (JWT bearer when available, else
/// <c>x-api-key</c>) and transparently refreshes the JWT on a 401, retrying the
/// request once. The refresh itself (and the single-flight dedup of concurrent
/// 401s) lives in <see cref="AuthRefresher"/>, shared with the SSE client. Port of
/// the auth/refresh logic in <c>api/client.rs</c> (<c>api_fetch</c> + <c>refresh_auth</c>).
/// </summary>
public sealed class AuthHandler(AuthSession session, AuthRefresher refresher, ILogger<AuthHandler> log)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = session.Token;
        AuthHeaders.Apply(request, token, session.ApiKey);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Auto-refresh on 401, only when we have a refresh token (i.e. JWT auth).
        if (response.StatusCode == HttpStatusCode.Unauthorized && session.RefreshToken is not null)
        {
            response.Dispose();
            log.LogDebug("401 received, attempting token refresh and retry");
            await refresher.RefreshAsync(token, cancellationToken).ConfigureAwait(false);

            var retry = await CloneAsync(request).ConfigureAwait(false);
            AuthHeaders.Apply(retry, session.Token, session.ApiKey);
            return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        }

        return response;
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
}
