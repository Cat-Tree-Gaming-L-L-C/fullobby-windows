using System.Net;

namespace Fullobby.Core.Api;

/// <summary>
/// Raised when an API call returns a non-success status. The message follows the
/// server's wire format ("API error: …" / "Update required: …") so it can be passed
/// straight to <see cref="ApiValidation.FriendlyError"/>. <see cref="StatusCode"/> is
/// the HTTP status when the failure came from a response (null for client-side throws
/// like validation), letting callers distinguish a definitive rejection (e.g. a 400/404
/// "session no longer active") from a transient/unknown failure.
/// </summary>
public sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public ApiException(string message) : base(message) { }

    public ApiException(string message, HttpStatusCode statusCode) : base(message) => StatusCode = statusCode;
}
