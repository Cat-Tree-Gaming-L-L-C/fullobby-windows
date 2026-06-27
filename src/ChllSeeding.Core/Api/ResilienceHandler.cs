using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Api;

/// <summary>
/// Retries transient HTTP failures with exponential backoff:
/// up to 3 attempts, 1s/2s backoff,
/// retrying on 429/503 (honoring <c>Retry-After</c> ≤ 60s) and on
/// connect/timeout errors. The per-request <see cref="HttpClient"/> timeout
/// (30s) supplies the timeout behaviour.
/// </summary>
public sealed class ResilienceHandler(ILogger<ResilienceHandler> log) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (attempt < RetryPolicy.MaxAttempts && RetryPolicy.IsRetryableStatus(response.StatusCode))
                {
                    var delay = RetryPolicy.ParseRetryAfter(response) ?? RetryPolicy.BackoffForAttempt(attempt);
                    log.LogWarning("Retryable status {Status} (attempt {Attempt}/{Max}), retrying in {Delay}s",
                        (int)response.StatusCode, attempt, RetryPolicy.MaxAttempts, delay);
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException e) when (attempt < RetryPolicy.MaxAttempts)
            {
                var delay = RetryPolicy.BackoffForAttempt(attempt);
                log.LogWarning(e, "Transient network error (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt, RetryPolicy.MaxAttempts, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException e) when (
                attempt < RetryPolicy.MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Timeout (not user cancellation): the HttpClient timeout fired.
                var delay = RetryPolicy.BackoffForAttempt(attempt);
                log.LogWarning(e, "Request timed out (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt, RetryPolicy.MaxAttempts, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
