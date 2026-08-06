using System.Text;
using System.Text.Json;
using Fullobby.Core.Api;

namespace Fullobby.Core.Tests;

public class JwtUtilTests
{
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Build a token with header.payload.signature where payload carries the given exp.</summary>
    private static string MakeToken(long? exp)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payloadObj = exp is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["exp"] = exp.Value };
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj)));
        return $"{header}.{payload}.signature";
    }

    [Fact]
    public void IsExpired_FutureExp_False()
    {
        var future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        Assert.False(JwtUtil.IsExpired(MakeToken(future)));
    }

    [Fact]
    public void IsExpired_PastExp_True()
    {
        var past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        Assert.True(JwtUtil.IsExpired(MakeToken(past)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    public void IsExpired_Malformed_True(string token) => Assert.True(JwtUtil.IsExpired(token));

    [Fact]
    public void IsExpired_MissingExpClaim_True() => Assert.True(JwtUtil.IsExpired(MakeToken(null)));

    [Fact]
    public void IsExpired_NonBase64Payload_True() => Assert.True(JwtUtil.IsExpired("aaa.!!!!.ccc"));
}
