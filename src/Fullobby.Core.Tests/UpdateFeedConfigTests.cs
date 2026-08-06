using Fullobby.Core;
using Fullobby.Core.Update;

namespace Fullobby.Core.Tests;

/// <summary>The update feed must be a hardbaked HTTPS origin — the updater fetches a signed manifest
/// from it, so a downgraded scheme or wrong host would undermine the pinned-key trust chain.</summary>
public class UpdateFeedConfigTests
{
    [Fact]
    public void DefaultFeed_IsHttps_AndExpectedHost()
    {
        var uri = new Uri(Branding.DefaultUpdateFeedBaseUrl);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("updates.fullobby.com", uri.Host);
    }

    [Fact]
    public void FeedBaseUrl_DefaultsToBranding_WhenNoOverride()
    {
        var prev = Environment.GetEnvironmentVariable("FULLOBBY_UPDATE_FEED_URL");
        Environment.SetEnvironmentVariable("FULLOBBY_UPDATE_FEED_URL", null);
        try
        {
            Assert.Equal(Branding.DefaultUpdateFeedBaseUrl, UpdateConfig.FeedBaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FULLOBBY_UPDATE_FEED_URL", prev);
        }
    }
}
