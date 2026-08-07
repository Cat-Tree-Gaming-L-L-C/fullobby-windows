using Fullobby.Core;
using Fullobby.Core.Activation;
using Fullobby.Core.Config;
using Fullobby.Core.Platform;
using Fullobby.Core.Scheduling;
using Fullobby.Core.Seeding;
using Fullobby.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Serilog;

namespace Fullobby.App;

public partial class App : Application
{
    /// <summary>Generic Host: DI container + (in later phases) background services.</summary>
    public static IHost AppHost { get; private set; } = null!;

    private MainWindow? _window;

    /// <summary>True when this process was started specifically to run an auto-seed (a scheduled-task
    /// or CLI <c>--autoseed</c> launch), as opposed to a normal user launch. Only then do we exit the
    /// app after re-arming on a scheduled pause, so the machine can go back to sleep.</summary>
    private readonly bool _launchedForAutoseed = HasAutoseedArg(Environment.GetCommandLineArgs());

    /// <summary>Guards against subscribing to the VM's resleep event more than once.</summary>
    private bool _resleepWired;

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
        Log.Information("Fullobby v{Version} starting",
            typeof(App).Assembly.GetName().Version?.ToString(3));

        // One-time migration of the retired global "Seeding Power Savings" Settings toggle into
        // the per-game Power Savings checkbox keys (before any UI reads them).
        try
        {
            EfficiencyPreference.MigrateLegacyGlobalToggle(
                AppHost.Services.GetRequiredService<ConfigService>());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Efficiency-mode preference migration failed");
        }

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

        // Register the fullobby:// protocol for this exe (refreshes the
        // installer's bootstrap registration with the WAS activation marker so
        // GetActivatedEventArgs reports ExtendedActivationKind.Protocol).
        // Re-registered on every start.
        try
        {
            ActivationRegistrationManager.RegisterForProtocolActivation(
                Branding.ProtocolScheme,
                Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
                Branding.ProductName,
                null);

            // The WAS registration above mints a per-exe-path ProgId and never removes the one it
            // made for a previous path, so moves/updates/dev-builds accumulate live handlers and the
            // fullobby:// "open with" picker fills up with stale duplicate Fullobby entries.
            // Prune every Fullobby handler that isn't the running exe so exactly one remains.
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var removed = ProtocolRegistration.PruneStaleHandlers(Path.GetFileName(exe), exe);
                if (removed.Count > 0)
                {
                    Log.Information("Pruned {Count} stale fullobby:// handler(s): {ProgIds}",
                        removed.Count, string.Join(", ", removed));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "fullobby:// protocol registration failed");
        }

        // Activations redirected from secondary instances (fullobby:// deep links)
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

            // Hard-exit safety net: WinUI 3 does not reliably terminate the process
            // when the last window closes (the tray helper window + host background
            // services keep the message loop alive), and a hosted service's StopAsync
            // can hang. Force the process down if the clean shutdown below stalls, so
            // "Quit" always actually exits instead of ghosting until Task Manager.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                Log.Warning("Shutdown didn't complete in time, forcing process exit");
                Log.CloseAndFlush();
                Environment.Exit(0);
            });

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

            // Clean shutdown finished — terminate now rather than relying on WinUI to
            // tear down the process once the last window and the tray icon are gone.
            Environment.Exit(0);
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
        // (fullobby:// deep link, or --autoseed from a scheduled task).
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());

        // Fallback for a fresh scheduled-task launch where the activation args don't carry the flag.
        if (HasAutoseedArg(Environment.GetCommandLineArgs()))
        {
            StartAutoseed();
        }
    }

    /// <summary>Missed-autoseed watchdog fired (off-thread) — marshal onto the UI and run it.</summary>
    private void OnAutoseedDue() => StartAutoseed();

    /// <summary>The auto-seed re-armed its wake for a moved window. If this process was launched just
    /// to seed (not an interactive session), release keep-awake and exit so the PC returns to sleep
    /// and the scheduled task wakes it again at the new window.</summary>
    private void OnResleepRequested()
    {
        if (!_launchedForAutoseed)
        {
            return; // interactive session — leave the app running, just re-armed
        }

        Log.Information("Auto-seed re-armed for a moved window; exiting so the PC can sleep");
        try { AppHost.Services.GetRequiredService<Core.Native.KeepAwake>().Release(); }
        catch (Exception ex) { Log.Warning(ex, "Keep-awake release before resleep failed"); }

        var queue = _window?.DispatcherQueue;
        if (queue is null)
        {
            Exit();
            return;
        }
        queue.TryEnqueue(Exit);
    }

    /// <summary>Marshal to the UI thread and kick off the (single, region-free) auto-seed countdown.</summary>
    private void StartAutoseed()
    {
        Log.Information("Auto-seed requested");
        var vm = AppHost.Services.GetRequiredService<ViewModels.SeedingViewModel>();
        if (!_resleepWired)
        {
            vm.ResleepRequested += OnResleepRequested;
            _resleepWired = true;
        }
        var queue = _window?.DispatcherQueue;
        if (queue is null)
        {
            _ = vm.RunAutoseedAsync();
            return;
        }
        queue.TryEnqueue(() =>
        {
            _window?.BringToFront();
            _ = vm.RunAutoseedAsync();
        });
    }

    /// <summary>Whether any token requests an auto-seed launch (--autoseed, plus legacy --autoseed-*/--seed-*).</summary>
    private static bool HasAutoseedArg(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (AutoSeedSlot.IsAutoseedArg(arg))
            {
                return true;
            }
        }
        return false;
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
        // A scheduled task launching a second instance forwards its --autoseed flag here.
        if (args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs autoseedLaunch
            && autoseedLaunch.Arguments is { Length: > 0 } rawArgs
            && HasAutoseedArg(rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            StartAutoseed();
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
            case DeepLinkAction.RegisterCallback reg:
                Log.Information("Deep link: register callback received");
                _ = account.HandleRegisterCallbackAsync(reg.State, reg.Token);
                break;
            case DeepLinkAction.Unknown unknown:
                // Strip query/fragment before logging: a malformed auth/register callback
                // (e.g. token present but refresh_token missing) lands here still carrying a
                // token in the query, which must never reach the log file.
                var safeUrl = Uri.TryCreate(unknown.Url, UriKind.Absolute, out var u)
                    ? u.GetLeftPart(UriPartial.Path)
                    : unknown.Url.Split('?', '#')[0];
                Log.Warning("Deep link: unknown action for {Url}", safeUrl);
                break;
            case null:
                Log.Warning("Ignored non-fullobby URI activation");
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

        // Rolling logs: a new file each day AND whenever a file hits 10 MB, so a runaway
        // log loop can't silently fill the disk (Serilog's default is a 1 GB cap per file
        // that then STOPS writing). ~14 files retained → total on-disk logs stay ≤ ~140 MB.
        Log.Logger = new LoggerConfiguration()
            // Verbose diagnostics (LogDebug — splash-bypass steps, PID transitions, 401 retries)
            // in dev builds; Information in Release so shipped logs stay lean.
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logsDir, "fullobby-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                // Core: config, API, native/tools, seeding engine, startup worker.
                services.AddFullobbyCore();

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
