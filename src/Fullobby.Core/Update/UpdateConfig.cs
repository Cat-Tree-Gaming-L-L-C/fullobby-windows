namespace Fullobby.Core.Update;

/// <summary>Resolves the self-update feed base URL (where the signed <c>latest.json</c> /
/// <c>latest-beta.json</c> manifests live).
///
/// <para>Release (shipped) builds are <b>hardbaked</b> to <see cref="Branding.DefaultUpdateFeedBaseUrl"/>
/// (always HTTPS) and cannot be redirected — the <c>FULLOBBY_UPDATE_FEED_URL</c> override is
/// compiled out entirely, so a stray environment variable can never point a shipped client's updater at
/// an attacker-controlled feed.</para>
///
/// <para>Debug builds honor the <c>FULLOBBY_UPDATE_FEED_URL</c> override (handy for pointing the
/// updater at a locally-served, locally-signed manifest during testing).</para></summary>
public static class UpdateConfig
{
#if DEBUG
    public static string FeedBaseUrl =>
        Environment.GetEnvironmentVariable("FULLOBBY_UPDATE_FEED_URL") is { Length: > 0 } overridden
            ? overridden.TrimEnd('/')
            : Branding.DefaultUpdateFeedBaseUrl;
#else
    public static string FeedBaseUrl => Branding.DefaultUpdateFeedBaseUrl;
#endif
}
