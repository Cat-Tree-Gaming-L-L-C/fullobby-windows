namespace ChllSeeding.Core.Api;

/// <summary>Resolves the seeding API base URL. Honors the <c>CHLL_SEEDING_API_URL</c>
/// environment override (handy for pointing at a local/staging backend), otherwise
/// falls back to <see cref="Branding.DefaultApiBaseUrl"/>.</summary>
public static class ApiConfig
{
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("CHLL_SEEDING_API_URL") is { Length: > 0 } overridden
            ? overridden.TrimEnd('/')
            : Branding.DefaultApiBaseUrl;
}
