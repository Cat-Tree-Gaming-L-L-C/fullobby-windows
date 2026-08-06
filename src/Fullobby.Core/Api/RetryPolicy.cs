using System.Net;

namespace Fullobby.Core.Api;

/// <summary>
/// Transient-failure retry rules.
/// Pure predicates so they can be unit-tested; the actual retrying happens in
/// <see cref="ResilienceHandler"/>.
/// </summary>
public static class RetryPolicy
{
    public const int MaxAttempts = 3;

    /// <summary>Backoff (seconds) indexed by attempt: 1s after attempt 1, 2s after attempt 2+.</summary>
    public static readonly int[] BackoffSecs = [1, 2];

    /// <summary>HTTP status codes that are safe to retry.</summary>
    public static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>Parse the <c>Retry-After</c> header as integer seconds. Integer form only
    /// (no HTTP-date), and only values in (0, 60] are honored.</summary>
    public static int? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }
        var raw = values.FirstOrDefault();
        if (raw is null || !int.TryParse(raw, out var secs))
        {
            return null;
        }
        return secs is > 0 and <= 60 ? secs : null;
    }

    /// <summary>Backoff delay for a given 1-based attempt number.</summary>
    public static int BackoffForAttempt(int attempt) =>
        BackoffSecs[Math.Min(attempt - 1, BackoffSecs.Length - 1)];
}
