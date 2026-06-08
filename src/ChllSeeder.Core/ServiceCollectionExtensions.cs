using ChllSeeder.Core.Api;
using ChllSeeder.Core.Bootstrap;
using ChllSeeder.Core.Config;
using ChllSeeder.Core.Native;
using ChllSeeder.Core.Seeding;
using ChllSeeder.Core.Servers;
using ChllSeeder.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        // Native + tools layer (process/window/input/Steam launch, efficiency mode, power).
        services.AddSingleton<ProcessMonitor>();
        services.AddSingleton<WindowFocus>();
        services.AddSingleton<Win11Input>();
        services.AddSingleton<SteamLauncher>();
        services.AddSingleton<PowerStatus>();
        services.AddSingleton<HllConfigBackupService>();
        services.AddSingleton<KeepAwake>();

        // Seeding engine: the state machine that orchestrates the native + API layers.
        services.AddSingleton<SeedingState>();
        services.AddSingleton<SeedingEngine>();

        // Startup worker: guest auth + server-list load + stats polling (Phase 2 → SSE).
        services.AddSingleton<AppBootstrapper>();
        services.AddHostedService(sp => sp.GetRequiredService<AppBootstrapper>());

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
