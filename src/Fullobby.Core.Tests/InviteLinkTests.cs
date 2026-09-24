using Fullobby.Core.Api;

namespace Fullobby.Core.Tests;

/// <summary>Tests for reading the token out of a pasted invite link.</summary>
public class InviteLinkTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("https://api.fullobby.com/invite#" + Token)]
    [InlineData("http://localhost:8080/invite#" + Token)]
    [InlineData("  https://api.fullobby.com/invite#" + Token + "  ")]
    [InlineData("https://api.fullobby.com/invite# " + Token + "\n")]
    [InlineData(Token)]
    [InlineData("  " + Token + "\t")]
    [InlineData("#" + Token)]
    public void ParseToken_AcceptsLinkOrBareToken(string input) =>
        Assert.Equal(Token, InviteLink.ParseToken(input));

    [Fact]
    public void ParseToken_LowercasesAnUppercasedPaste() =>
        Assert.Equal(Token, InviteLink.ParseToken(Token.ToUpperInvariant()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.fullobby.com/invite")]
    [InlineData("https://api.fullobby.com/invite#")]
    [InlineData("0123456789abcdef")] // too short
    [InlineData(Token + "0")] // too long
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")] // not hex
    [InlineData("TEST-2345-CODE")] // a join code pasted into the link box
    public void ParseToken_RejectsWhatIsNotAToken(string? input) =>
        Assert.Null(InviteLink.ParseToken(input));

    [Fact]
    public void ParseToken_RejectsAnInnerSpace() =>
        Assert.Null(InviteLink.ParseToken(Token[..32] + " " + Token[32..]));
}
