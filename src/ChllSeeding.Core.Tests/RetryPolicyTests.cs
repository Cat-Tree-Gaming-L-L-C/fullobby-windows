using System.Net;
using ChllSeeding.Core.Api;

namespace ChllSeeding.Core.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public void IsRetryableStatus(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, RetryPolicy.IsRetryableStatus(status));

    private static HttpResponseMessage WithRetryAfter(string? value)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (value is not null)
        {
            resp.Headers.TryAddWithoutValidation("Retry-After", value);
        }
        return resp;
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("1", 1)]
    public void ParseRetryAfter_Valid(string header, int expected) =>
        Assert.Equal(expected, RetryPolicy.ParseRetryAfter(WithRetryAfter(header)));

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("120")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void ParseRetryAfter_Rejected(string? header) =>
        Assert.Null(RetryPolicy.ParseRetryAfter(WithRetryAfter(header)));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)] // clamps to last backoff value
    public void BackoffForAttempt(int attempt, int expected) =>
        Assert.Equal(expected, RetryPolicy.BackoffForAttempt(attempt));
}
