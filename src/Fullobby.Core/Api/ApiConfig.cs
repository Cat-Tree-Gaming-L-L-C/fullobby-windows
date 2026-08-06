namespace Fullobby.Core.Api;

/// <summary>Resolves the seeding API base URL.
///
/// <para>Release (shipped) builds are <b>hardbaked</b> to <see cref="Branding.DefaultApiBaseUrl"/>
/// (always HTTPS) and cannot be redirected — the <c>FULLOBBY_API_URL</c> override is
/// compiled out entirely, so a stray environment variable can never point a shipped client at
/// an attacker-controlled host.</para>
///
/// <para>Debug builds honor the <c>FULLOBBY_API_URL</c> environment override (handy for
/// pointing at a local mock or staging backend — see <c>scripts/run-local.ps1</c>).</para></summary>
public static class ApiConfig
{
#if DEBUG
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("FULLOBBY_API_URL") is { Length: > 0 } overridden
            ? overridden.TrimEnd('/')
            : Branding.DefaultApiBaseUrl;
#else
    public static string BaseUrl => Branding.DefaultApiBaseUrl;
#endif
}
