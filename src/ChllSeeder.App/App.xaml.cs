using ChllSeeder.Core;
using ChllSeeder.Core.Activation;
using ChllSeeder.Core.Platform;
using ChllSeeder.Core.Scheduling;
using ChllSeeder.Core.Seeding;
using ChllSeeder.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Serilog;

namespace ChllSeeder.App;

public partial class App : Application
{
    /// <summary>Generic Host: DI container + (in later phases) background services.</summary>
    public static IHost AppHost { get; private set; } = null!;

    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        AppHost = BuildHost();

        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled exception: {Message}", e.Message);
            Log.CloseAndFlush();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppHost.Start();
        Log.Information("CHLL Seeder v{Version} starting",
            typeof(App).Assembly.GetName().Version?.ToString(3));

        // Efficiency-mode crash recovery: if a previous run was killed mid-seed with degraded
        // graphics settings applied, restore the user's real settings now (before any UI). Port of
        // check_and_restore_on_startup(); the one-shot notice is surfaced once the window is up.
        var backup = AppHost.Services.GetRequiredService<HllConfigBackupService>();
        try
        {
            backup.CheckAndRestoreOnStartup();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Efficiency-mode startup recovery failed");
        }

        // If "Start with Windows" is enabled but points at a stale exe path (e.g. after an update
        // moved the install), refresh it. Port of update_startup_path_if_needed().
        try
        {
            AppHost.Services.GetRequiredService<StartupRegistry>().UpdatePathIfNeeded();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup path refresh failed");
        }

        // Fire a missed (scheduler-skipped or post-wake) auto-seed when the watchdog detects one.
        AppHost.Services.GetRequiredService<MissedAutoseedMonitor>().AutoseedDue += OnAutoseedDue;

        // Register the chllseeder:// protocol for this exe (refreshes the
        // installer's bootstrap registration with the WAS activation marker so
        // GetActivatedEventArgs reports ExtendedActivationKind.Protocol).
        // Mirrors the Rust app's register_deep_link_protocol() on every start.
        try
        {
            ActivationRegistrationManager.RegisterForProtocolActivation(
                Branding.ProtocolScheme,
                Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
                Branding.ProductName,
                null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "chllseeder:// protocol registration failed");
        }

        // Activations redirected from secondary instances (chllseeder:// deep links)
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;

        // Desktop toasts: register the unpackaged handler and bring the window
        // forward when the user clicks one of our notifications.
        var toasts = AppHost.Services.GetRequiredService<Services.ToastService>();
        toasts.Register();
        toasts.Activated += () => _window?.DispatcherQueue.TryEnqueue(() => _window.BringToFront());

        _window = AppHost.Services.GetRequiredService<MainWindow>();
        _window.Closed += (_, _) =>
        {
            // Reached only on a real quit — MainWindow's close handler cancels the
            // close and hides to tray when close_to_tray is enabled (the default).
            Log.Information("Main window closed, shutting down");

            // End any open seeding session (analytics, fire-and-forget so it doesn't block exit).
            try
            {
                AppHost.Services.GetRequiredService<HeartbeatService>().StopFireAndForget("app_exit");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Heartbeat stop on exit failed");
            }

            // If efficiency mode is applied, kill the game and restore the user's real
            // graphics settings so HLL isn't left degraded after we exit.
            try
            {
                AppHost.Services.GetRequiredService<SeedingEngine>()
                    .CleanupEfficiencyOnExitAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Efficiency-mode cleanup on exit failed");
            }

            try
            {
                AppHost.Services.GetRequiredService<Services.ToastService>().Unregister();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Toast unregister on exit failed");
            }

            AppHost.StopAsync().GetAwaiter().GetResult();
            Log.CloseAndFlush();
        };
        _window.Activate();

        // Restore any persisted account session in the background (guest API key or JWT),
        // then refresh linked providers/Steam IDs. Non-fatal — failures just leave us signed out.
        var account = AppHost.Services.GetRequiredService<ViewModels.AccountViewModel>();
        _ = account.RestoreSessionAsync();

        // Surface the efficiency-mode crash-recovery notice now that the toast host exists.
        var notice = backup.TakeStartupRestoreNotice();
        if (notice is not null)
        {
            AppHost.Services.GetRequiredService<Services.InAppToastService>().Info(notice);
        }

        // The first instance itself may have been protocol-launched or scheduled-task-launched
        // (chllseeder:// deep link, or --autoseed-na/--autoseed-eu from a scheduled task).
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());

        // Fallback for a fresh scheduled-task launch where the activation args don't carry the flag.
        if (ExtractAutoseedRegion(Environment.GetCommandLineArgs()) is { } cliRegion)
        {
            StartAutoseed(cliRegion);
        }
    }

    /// <summary>Missed-autoseed watchdog fired (off-thread) — marshal onto the UI and run it.</summary>
    private void OnAutoseedDue(string region) => StartAutoseed(region);

    /// <summary>Marshal to the UI thread and kick off the auto-seed countdown for a region.</summary>
    private void StartAutoseed(string region)
    {
        Log.Information("Auto-seed requested for {Region}", region.ToUpperInvariant());
        var vm = AppHost.Services.GetRequiredService<ViewModels.SeedingViewModel>();
        var queue = _window?.DispatcherQueue;
        if (queue is null)
        {
            _ = vm.RunAutoseedAsync(region);
            return;
        }
        queue.TryEnqueue(() =>
        {
            _window?.BringToFront();
            _ = vm.RunAutoseedAsync(region);
        });
    }

    /// <summary>Find the auto-seed region in a token list, or null. Accepts --autoseed-* (+ legacy --seed-*).</summary>
    private static string? ExtractAutoseedRegion(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (AutoSeedSlot.ByCliArg(arg) is { } slot)
            {
                return slot.Region;
            }
        }
        return null;
    }

    /// <summary>Raised on a non-UI thread; marshal before touching the window.</summary>
    private void OnRedirectedActivation(object? sender, AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            HandleActivation(args);
            _window.BringToFront();
        });
    }

    private void HandleActivation(AppActivationArguments args)
    {
        // A scheduled task launching a second instance forwards its --autoseed-* flag here.
        if (args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs autoseedLaunch
            && autoseedLaunch.Arguments is { Length: > 0 } rawArgs
            && ExtractAutoseedRegion(rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) is { } region)
        {
            StartAutoseed(region);
            return;
        }

        var uri = ExtractDeepLinkUri(args);
        if (uri is null)
        {
            return; // plain launch, nothing to do
        }

        // Route OAuth deep links into the account VM. Runs on the UI thread (OnLaunched, or
        // marshalled by OnRedirectedActivation). The VM marshals its own state writes; never
        // log token values.
        var account = AppHost.Services.GetRequiredService<ViewModels.AccountViewModel>();
        switch (DeepLinkParser.Parse(uri))
        {
            case DeepLinkAction.AuthCallback auth:
                Log.Information("Deep link: auth callback received");
                _ = account.HandleAuthCallbackAsync(auth.State, auth.Token, auth.RefreshToken);
                break;
            case DeepLinkAction.LinkCallback link:
                Log.Information("Deep link: link callback for provider {Provider}", link.Provider);
                _ = account.HandleLinkCallbackAsync(link.Provider);
                break;
            case DeepLinkAction.Unknown unknown:
                Log.Warning("Deep link: unknown action for {Url}", unknown.Url);
                break;
            case null:
                Log.Warning("Ignored non-chllseeder URI activation");
                break;
        }
    }

    private static string? ExtractDeepLinkUri(AppActivationArguments args)
    {
        // Proper protocol activation (WAS-registered handler).
        // Unpackaged WAS quirk: cast activation Data via the interface, not the class.
        if (args.Kind == ExtendedActivationKind.Protocol
            && args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol)
        {
            return protocol.Uri.ToString();
        }

        // Fallback: a plain `"exe" "url"` registration (e.g. the installer's
        // bootstrap keys before first run) surfaces as a Launch activation with
        // the URI somewhere in the raw command line.
        if (args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            return launch.Arguments?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim('"'))
                .FirstOrDefault(a => a.StartsWith(Branding.ProtocolPrefix, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static IHost BuildHost()
    {
        var logsDir = Branding.LogsDir;
        Directory.CreateDirectory(logsDir);

        // Rolling daily file logs, 7-file retention (matches the Rust app)
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logsDir, "chll-seeder-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                // Core: config, API, native/tools, seeding engine, startup worker.
                services.AddChllSeederCore();

                services.AddSingleton<Services.InAppToastService>();
                services.AddSingleton<Services.ToastService>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<ViewModels.SeedingViewModel>();
                services.AddSingleton<ViewModels.AccountViewModel>();
                services.AddSingleton<ViewModels.LeaderboardViewModel>();
            })
            .Build();
    }
}
