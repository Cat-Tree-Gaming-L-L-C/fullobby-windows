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
        services.AddSingleton<AuthRefresher>();
        services.AddSingleton<Activation.OAuthStateStore>();
        services.AddSingleton<SeedingStatusCache>();
        services.AddSingleton<ServerStore>();
        services.AddSingleton<LiveStats>();
        services.AddSingleton<SseConnectionState>();

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
        services.AddSingleton<HeartbeatService>();

        // Startup worker: guest auth + server-list load + stats polling fallback.
        services.AddSingleton<AppBootstrapper>();
        services.AddHostedService(sp => sp.GetRequiredService<AppBootstrapper>());

        // Live data: SSE stats stream (primary; the bootstrapper poll is the fallback).
        services.AddSingleton<SseStreamClient>();
        services.AddHostedService(sp => sp.GetRequiredService<SseStreamClient>());

        services.AddTransient<AuthHandler>();
        services.AddTransient<ResilienceHandler>();

        var version = typeof(SeedingApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var userAgent = $"CHLLSeeder/{version}";

        services.AddHttpClient<SeedingApiClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                client.DefaultRequestHeaders.Add("x-client-version", version);
            })
            // Outer → inner: auth/refresh wraps transient-retry wraps the socket handler.
            .AddHttpMessageHandler<AuthHandler>()
            .AddHttpMessageHandler<ResilienceHandler>();

        // Refresh client: resilience only, NO AuthHandler (a refresh must not recurse through auth).
        services.AddHttpClient(AuthRefresher.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                client.DefaultRequestHeaders.Add("x-client-version", version);
            })
            .AddHttpMessageHandler<ResilienceHandler>();

        // SSE client: effectively no timeout (a stream stays open), no delegating handlers
        // (auth headers are applied per-connection; the 30s timeout would kill the stream).
        services.AddHttpClient(SseStreamClient.ClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                client.DefaultRequestHeaders.Add("x-client-version", version);
            });

        return services;
    }
}
