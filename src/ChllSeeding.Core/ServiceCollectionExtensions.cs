using ChllSeeding.Core.Api;
using ChllSeeding.Core.Bootstrap;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Native;
using ChllSeeding.Core.Platform;
using ChllSeeding.Core.Scheduling;
using ChllSeeding.Core.Seeding;
using ChllSeeding.Core.Servers;
using ChllSeeding.Core.Tools;
using ChllSeeding.Core.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChllSeeding.Core;

/// <summary>DI registration for the non-UI Core services. Call from the app host.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChllSeedingCore(this IServiceCollection services)
    {
        services.AddSingleton<ConfigService>();
        services.AddSingleton<SeedingConfigProvider>();
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
        services.AddSingleton<ManualBackupService>();
        services.AddSingleton<KeepAwake>();

        // Self-updater (Phase 5): release check + installer download/verify/launch.
        services.AddSingleton<UpdaterService>();

        // Automation & tools (Phase 4): startup registry + auto-seed scheduling.
        services.AddSingleton<StartupRegistry>();
        services.AddSingleton<ScheduledTaskService>();
        services.AddSingleton<AutoSeedService>();
        services.AddSingleton<AutoSeedState>();
        services.AddSingleton<MissedAutoseedMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<MissedAutoseedMonitor>());

        // Seeding engine: the state machine that orchestrates the native + API layers.
        services.AddSingleton<SeedingState>();
        services.AddSingleton<SeedingEngine>();
        services.AddSingleton<HeartbeatService>();

        // Self-updater (Phase 5): check + download/verify/launch the Inno setup.exe.
        services.AddSingleton<UpdaterService>();

        // Startup worker: guest auth + server-list load + stats polling fallback.
        services.AddSingleton<AppBootstrapper>();
        services.AddHostedService(sp => sp.GetRequiredService<AppBootstrapper>());

        // Live data: SSE stats stream (primary; the bootstrapper poll is the fallback).
        services.AddSingleton<SseStreamClient>();
        services.AddHostedService(sp => sp.GetRequiredService<SseStreamClient>());

        services.AddTransient<AuthHandler>();
        services.AddTransient<ResilienceHandler>();

        var version = typeof(SeedingApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var userAgent = $"CHLLSeeding/{version}";

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

        // Updater client: long timeout (installer downloads can be large), resilience but NO auth
        // (release endpoints are public). Port of the Rust 300s download timeout.
        services.AddHttpClient(UpdaterService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(300);
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

        // Updater client: unauthenticated (the release feed is public; downloads hit GitHub's CDN),
        // with a long timeout to cover a large installer download. The size cap bounds the body.
        services.AddHttpClient(UpdaterService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                client.DefaultRequestHeaders.Add("x-client-version", version);
            });

        return services;
    }
}
