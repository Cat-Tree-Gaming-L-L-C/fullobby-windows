using System.Net.Http.Headers;

namespace Fullobby.Core.Api;

/// <summary>
/// Applies the current auth credentials to an outgoing request: JWT bearer when
/// available, else the guest <c>x-api-key</c>. Shared by <see cref="AuthHandler"/>
/// (the typed-client delegating handler) and <see cref="SseStreamClient"/>, which
/// builds its own requests on a handler-less client and so can't rely on the handler.
/// </summary>
public static class AuthHeaders
{
    public static void Apply(HttpRequestMessage request, string? token, string? apiKey)
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
}
