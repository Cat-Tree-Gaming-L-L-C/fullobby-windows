using ChllSeeding.Core;
using ChllSeeding.Core.Update;

namespace ChllSeeding.Core.Tests;

/// <summary>The update feed must be a hardbaked HTTPS origin — the updater fetches a signed manifest
/// from it, so a downgraded scheme or wrong host would undermine the pinned-key trust chain.</summary>
public class UpdateFeedConfigTests
{
    [Fact]
    public void DefaultFeed_IsHttps_AndExpectedHost()
    {
        var uri = new Uri(Branding.DefaultUpdateFeedBaseUrl);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("updates.comp-hll.org", uri.Host);
    }

    [Fact]
    public void FeedBaseUrl_DefaultsToBranding_WhenNoOverride()
    {
        var prev = Environment.GetEnvironmentVariable("CHLL_SEEDING_UPDATE_FEED_URL");
        Environment.SetEnvironmentVariable("CHLL_SEEDING_UPDATE_FEED_URL", null);
        try
        {
            Assert.Equal(Branding.DefaultUpdateFeedBaseUrl, UpdateConfig.FeedBaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHLL_SEEDING_UPDATE_FEED_URL", prev);
        }
    }
}
