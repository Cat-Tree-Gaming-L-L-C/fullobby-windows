using System.Reflection;
using ChllSeeding.Core.Update;

namespace ChllSeeding.Core.Tests;

/// <summary>Tests for resolving the app version used by the self-update comparison — in particular
/// that a pre-release suffix survives so beta→beta updates are still detected.</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.4-beta.1+abc1234", "1.2.4-beta.1")] // SourceLink revision stripped
    [InlineData("1.2.4+abc1234", "1.2.4")]
    [InlineData("1.2.4-beta.1", "1.2.4-beta.1")]         // no metadata → unchanged
    [InlineData("1.2.4", "1.2.4")]
    [InlineData("", "")]
    public void StripBuildMetadata_Cases(string input, string expected) =>
        Assert.Equal(expected, AppVersion.StripBuildMetadata(input));

    [Fact]
    public void ForUpdateCheck_ReadsInformationalVersion_StrippingMetadata()
    {
        // Prefers the informational version (the SDK generates the attribute from <Version>) and drops
        // any "+revision" the build appends — without hardcoding a value that would need csproj surgery.
        var asm = typeof(AppVersionTests).Assembly;
        var raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var result = AppVersion.ForUpdateCheck(asm);
        Assert.Equal(AppVersion.StripBuildMetadata(raw), result);
        Assert.DoesNotContain('+', result);
    }

    [Fact]
    public void BetaChain_IsOfferedAfterFix()
    {
        // Regression guard: a beta build reporting its full informational version must still see the
        // next beta as an available update (the numeric-only core would have looked like a downgrade).
        var current = AppVersion.StripBuildMetadata("1.2.4-beta.1+abc1234");
        Assert.True(UpdateValidation.IsUpdateAvailable(current, "1.2.4-beta.2"));
        // And the final release is offered over a beta of the same core.
        Assert.True(UpdateValidation.IsUpdateAvailable(current, "1.2.4"));
    }
}
