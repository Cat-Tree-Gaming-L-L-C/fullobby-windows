using ChllSeeder.Core.Api;
using ChllSeeder.Core.Config;
using ChllSeeder.Core.Servers;
using Microsoft.Extensions.DependencyInjection;

namespace ChllSeeder.Core;

/// <summary>DI registration for the non-UI Core services. Call from the app host.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChllSeederCore(this IServiceCollection services)
    {
        services.AddSingleton<ConfigService>();
        services.AddSingleton<AuthSession>();
        services.AddSingleton<SeedingStatusCache>();
        services.AddSingleton<ServerStore>();

        services.AddTransient<AuthHandler>();
        services.AddTransient<ResilienceHandler>();

        var version = typeof(SeedingApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        services.AddHttpClient<SeedingApiClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"CHLLSeeder/{version}");
                client.DefaultRequestHeaders.Add("x-client-version", version);
            })
            // Outer → inner: auth/refresh wraps transient-retry wraps the socket handler.
            .AddHttpMessageHandler<AuthHandler>()
            .AddHttpMessageHandler<ResilienceHandler>();

        return services;
    }
}
