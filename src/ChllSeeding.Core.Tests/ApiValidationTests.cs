using System.Text;
using ChllSeeding.Core.Api;

namespace ChllSeeding.Core.Tests;

public class ApiValidationTests
{
    // ── FriendlyError ───────────────────────────────────────────────

    [Theory]
    [InlineData("dns error: failed to lookup address", "Could not reach server — check your internet connection")]
    [InlineData("No such host is known", "Could not reach server — check your internet connection")]
    [InlineData("operation timed out", "Request timed out — the server may be slow, try again")]
    [InlineData("Timeout waiting for response", "Request timed out — the server may be slow, try again")]
    [InlineData("Connection refused (os error 111)", "Server is not responding — try again later")]
    [InlineData("connect error: Connection reset", "Network error — check your internet connection")]
    public void FriendlyError_Network(string input, string expected) =>
        Assert.Equal(expected, ApiValidation.FriendlyError(input));

    [Fact]
    public void FriendlyError_UpdateRequired_Passthrough()
    {
        const string msg = "Update required: minimum version is 2.0.0. Please download the latest release.";
        Assert.Equal(msg, ApiValidation.FriendlyError(msg));
    }

    [Fact]
    public void FriendlyError_ApiJson_ExtractsErrorField() =>
        Assert.Equal("Invalid credentials",
            ApiValidation.FriendlyError("""API error: {"error": "Invalid credentials", "code": 401}"""));

    [Fact]
    public void FriendlyError_ApiNonJson_Passthrough() =>
        Assert.Equal("API error: Internal Server Error",
            ApiValidation.FriendlyError("API error: Internal Server Error"));

    [Fact]
    public void FriendlyError_TruncatesLong()
    {
        var result = ApiValidation.FriendlyError(new string('A', 100));
        Assert.Equal(80, result.Length);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void FriendlyError_ShortPassthrough() =>
        Assert.Equal("Something went wrong", ApiValidation.FriendlyError("Something went wrong"));

    [Fact]
    public void FriendlyError_MultibyteUtf8_TruncatesWithoutSplitting()
    {
        var result = ApiValidation.FriendlyError(new string('é', 100));
        Assert.EndsWith("...", result);
        Assert.Equal(80, result.EnumerateRunes().Count());
    }

    // ── ValidateDisplayName ─────────────────────────────────────────

    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob 123")]
    [InlineData("my-name_here.ok")]
    [InlineData("ab")]
    public void ValidateName_Valid(string name) => Assert.Null(ApiValidation.ValidateDisplayName(name));

    [Fact]
    public void ValidateName_MaxLength_Valid() => Assert.Null(ApiValidation.ValidateDisplayName(new string('a', 32)));

    [Theory]
    [InlineData("a")]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_TooShort(string name) =>
        Assert.Equal("Name must be at least 2 characters", ApiValidation.ValidateDisplayName(name));

    [Fact]
    public void ValidateName_TooLong() =>
        Assert.Equal("Name must be 32 characters or fewer", ApiValidation.ValidateDisplayName(new string('a', 33)));

    [Fact]
    public void ValidateName_InvalidChars()
    {
        Assert.Contains("'@'", ApiValidation.ValidateDisplayName("hello@world")!);
        Assert.NotNull(ApiValidation.ValidateDisplayName("name#tag"));
        Assert.NotNull(ApiValidation.ValidateDisplayName("emoji😀"));
    }

    [Theory]
    [InlineData("xNiggerx")]
    [InlineData("FAGGOT")]
    public void ValidateName_Profanity(string name) =>
        Assert.Equal("Name contains inappropriate language", ApiValidation.ValidateDisplayName(name));

    [Fact]
    public void ValidateName_TrimsWhitespace() => Assert.Null(ApiValidation.ValidateDisplayName("  Alice  "));

    // ── OAuth URL ───────────────────────────────────────────────────

    [Fact]
    public void OAuthUrl_Steam()
    {
        var url = ApiValidation.GetOAuthUrl("steam", "state123")!;
        Assert.Contains("/api/auth/steam", url);
        Assert.EndsWith("?state=state123", url);
    }

    [Fact]
    public void OAuthUrl_Format()
    {
        var url = ApiValidation.GetOAuthUrl("steam", "state");
        Assert.Equal($"{ApiConfig.BaseUrl}/api/auth/steam?state=state", url);
    }

    [Theory]
    [InlineData("twitch")]
    [InlineData("")]
    [InlineData("../evil")]
    public void OAuthUrl_InvalidProvider_Null(string provider) =>
        Assert.Null(ApiValidation.GetOAuthUrl(provider, "state"));

    [Fact]
    public void OAuthUrl_EncodesState()
    {
        var url = ApiValidation.GetOAuthUrl("steam", "state with spaces&special=chars")!;
        Assert.DoesNotContain(' ', url);
        Assert.Contains("state%20with%20spaces%26special%3Dchars", url);
    }

    [Fact]
    public void BaseUrl_IsHttpsNoTrailingSlash()
    {
        Assert.StartsWith("https://", ApiConfig.BaseUrl);
        Assert.False(ApiConfig.BaseUrl.EndsWith('/'));
    }

    // ── ID / provider validators ────────────────────────────────────

    [Theory]
    [InlineData("76561198000000000")]
    [InlineData("1")]
    [InlineData("12345678901234567890")]
    public void SteamId_Valid(string id) => Assert.True(ApiValidation.IsValidSteamId(id));

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    [InlineData("abc123")]
    [InlineData("1234-5678")]
    [InlineData("../etc/passwd")]
    public void SteamId_Invalid(string id) => Assert.False(ApiValidation.IsValidSteamId(id));

    [Theory]
    [InlineData("abc123")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("a")]
    public void UserId_Valid(string id) => Assert.True(ApiValidation.IsValidUserId(id));

    [Theory]
    [InlineData("")]
    [InlineData("user/path")]
    [InlineData("user;drop")]
    [InlineData("user name")]
    public void UserId_Invalid(string id) => Assert.False(ApiValidation.IsValidUserId(id));

    [Fact]
    public void UserId_TooLong() => Assert.False(ApiValidation.IsValidUserId(new string('a', 65)));

    [Theory]
    [InlineData("steam")]
    [InlineData("discord")]
    [InlineData("epic")]
    [InlineData("xbox")]
    public void Provider_Valid(string p) => Assert.True(ApiValidation.IsValidProvider(p));

    [Theory]
    [InlineData("epic")]
    [InlineData("xbox")]
    public void OAuthUrl_EpicXbox(string provider)
    {
        var url = ApiValidation.GetOAuthUrl(provider, "state")!;
        Assert.Equal($"{ApiConfig.BaseUrl}/api/auth/{provider}?state=state", url);
    }

    [Theory]
    [InlineData("twitch")]
    [InlineData("")]
    [InlineData("../evil")]
    public void Provider_Invalid(string p) => Assert.False(ApiValidation.IsValidProvider(p));
}
