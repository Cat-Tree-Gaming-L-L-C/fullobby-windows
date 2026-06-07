using ChllSeeder.Core.Activation;

namespace ChllSeeder.Core.Tests;

/// <summary>Port of the deep_link.rs test suite, rebranded to chllseeder://.</summary>
public class DeepLinkParserTests
{
    [Fact]
    public void ParseValidAuthCallback()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/callback?token=abc123&refresh_token=def456");

        var auth = Assert.IsType<DeepLinkAction.AuthCallback>(action);
        Assert.Equal("abc123", auth.Token);
        Assert.Equal("def456", auth.RefreshToken);
        Assert.Null(auth.State);
    }

    [Fact]
    public void ParseAuthCallbackWithState()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/callback?token=abc&refresh_token=def&state=xyz789");

        var auth = Assert.IsType<DeepLinkAction.AuthCallback>(action);
        Assert.Equal("abc", auth.Token);
        Assert.Equal("def", auth.RefreshToken);
        Assert.Equal("xyz789", auth.State);
    }

    [Fact]
    public void ParseAuthCallbackMissingRefreshToken()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/callback?token=abc123");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ParseAuthCallbackEmptyToken()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/callback?token=&refresh_token=def");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ParseAuthCallbackEmptyRefreshToken()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/callback?token=abc&refresh_token=");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ParseValidLinkCallbackSteam()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/link-callback?provider=steam&provider_id=12345");

        var link = Assert.IsType<DeepLinkAction.LinkCallback>(action);
        Assert.Equal("steam", link.Provider);
        Assert.Equal("12345", link.ProviderId);
    }

    [Fact]
    public void ParseValidLinkCallbackDiscord()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/link-callback?provider=discord&provider_id=99999");

        var link = Assert.IsType<DeepLinkAction.LinkCallback>(action);
        Assert.Equal("discord", link.Provider);
        Assert.Equal("99999", link.ProviderId);
    }

    [Fact]
    public void ParseLinkCallbackInvalidProvider()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/link-callback?provider=twitch&provider_id=123");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ParseLinkCallbackMissingProviderId()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/link-callback?provider=steam");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ParseUrlEncodedParams()
    {
        var action = DeepLinkParser.Parse("chllseeder://auth/callback?token=abc%20def&refresh_token=ghi%26jkl");

        var auth = Assert.IsType<DeepLinkAction.AuthCallback>(action);
        Assert.Equal("abc def", auth.Token);
        Assert.Equal("ghi&jkl", auth.RefreshToken);
    }

    [Fact]
    public void ParseNonChllSeederSchemeReturnsNull()
    {
        Assert.Null(DeepLinkParser.Parse("https://example.com"));
        Assert.Null(DeepLinkParser.Parse(""));
        Assert.Null(DeepLinkParser.Parse("hll://auth/callback"));
    }

    [Fact]
    public void ParseOldEspritSchemeReturnsNull()
    {
        // Clean break from the old brand: the legacy scheme must not parse.
        Assert.Null(DeepLinkParser.Parse("espritseeder://auth/callback?token=abc&refresh_token=def"));
    }

    [Fact]
    public void ParseUnknownPath()
    {
        var action = DeepLinkParser.Parse("chllseeder://some/random/path");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ParseWhitespaceTrimmed()
    {
        var action = DeepLinkParser.Parse("  chllseeder://auth/callback?token=abc&refresh_token=def  ");

        Assert.IsType<DeepLinkAction.AuthCallback>(action);
    }

    [Fact]
    public void OversizedParameterIsRejected()
    {
        var bigToken = new string('a', 5000); // > 4096 cap
        var action = DeepLinkParser.Parse($"chllseeder://auth/callback?token={bigToken}&refresh_token=def");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }

    [Fact]
    public void ExcessParametersAreIgnored()
    {
        // 20 junk params first — the 16-param cap must drop the trailing real ones.
        var junk = string.Join('&', Enumerable.Range(0, 20).Select(i => $"junk{i}=x"));
        var action = DeepLinkParser.Parse($"chllseeder://auth/callback?{junk}&token=abc&refresh_token=def");

        Assert.IsType<DeepLinkAction.Unknown>(action);
    }
}
