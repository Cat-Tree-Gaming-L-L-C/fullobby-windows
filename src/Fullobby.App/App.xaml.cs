using Fullobby.Core;
using Fullobby.Core.Activation;
using Fullobby.Core.Api;
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
    /// or CLI <c>--autoseed</c> launch), as opposed to a normal user launch. Only then does the app
    /// shut down once that auto-seed ends without seeding — re-armed for a moved window, or refused
    /// outright — so the machine can go back to sleep. See <see cref="EndAutoseedLaunch"/>.</summary>
    private readonly bool _launchedForAutoseed = HasAutoseedArg(Environment.GetCommandLineArgs());

    /// <summary>True when this process was launched by the "Start with Windows" entry, which passes
    /// <see cref="Branding.MinimizedArg"/> — the app comes up in the tray and the window is left
    /// unshown. Nothing else passes the flag: a launch the user asked for gets a window.</summary>
    private readonly bool _startMinimized = HasMinimizedArg(Environment.GetCommandLineArgs());

    /// <summary>Guards against subscribing to the VM's auto-seed lifecycle events more than once.</summary>
    private bool _autoseedEventsWired;

    /// <summary>The startup session restore, kept so the auto-seed path can wait for it before
    /// judging whether onboarding is genuinely incomplete — it heals a completion flag lost to a
    /// crash and arms the network gate, both of which move <c>ShowOnboarding</c>.</summary>
    private Task _sessionRestore = Task.CompletedTask;

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

        // Fire a missed (scheduler-skipped or post-wake) auto-seed when the watchdog detects one.
        AppHost.Services.GetRequiredService<MissedAutoseedMonitor>().AutoseedDue += OnAutoseedDue;

        // Tell an operator a ready check is waiting on them. Without this the only notice is the
        // bot's Discord DM, so a community running no Discord — or an operator with closed DMs —
        // never hears about a check that is blocking their own server from being seeded.
        AppHost.Services.GetRequiredService<ReadyCheckMonitor>().ChecksDue += OnReadyChecksDue;

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
        // A "Start with Windows" launch stays in the tray — never activating the window is what
        // keeps it off screen (a WinUI window is created hidden and Activate() is what shows it), so
        // there's no show-then-hide flash at sign-in. The tray icon is live regardless: MainWindow's
        // constructor ForceCreate()s it, and its left-click / "Show" command runs BringToFront,
        // which is then the window's first Activate(). What that defers is RootGrid.Loaded — and
        // with it the unprotected-secrets dialog, which already re-checks there because it can't
        // show without a XamlRoot. An auto-seed launch shows the window through StartAutoseed.
        if (_startMinimized)
        {
            Log.Information("Launched with {Arg} — starting in the tray", Branding.MinimizedArg);
        }
        else
        {
            _window.Activate();
        }

        // Restore any persisted account session in the background (guest API key or JWT),
        // then refresh linked providers/Steam IDs. Non-fatal — failures just leave us signed out.
        var account = AppHost.Services.GetRequiredService<ViewModels.AccountViewModel>();
        _sessionRestore = account.RestoreSessionAsync();

        // Deferred startup housekeeping. Nothing on the first frame depends on either of these, and
        // both hit the registry: PruneStaleHandlers enumerates every subkey of HKCU\Software\Classes
        // (routinely 1,000–3,000 of them), which is not something to do on the UI thread before the
        // window is up. The rest of the startup block stays synchronous because it is documented as
        // having to run before any UI reads config (the efficiency migration and crash recovery).
        _ = Task.Run(() =>
        {
            // "Start with Windows" pointing at a stale exe path (e.g. an update moved the install).
            try
            {
                AppHost.Services.GetRequiredService<StartupRegistry>().UpdatePathIfNeeded();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Startup path refresh failed");
            }

            // The WAS registration mints a per-exe-path ProgId and never removes the one it made for
            // a previous path, so moves/updates/dev-builds accumulate live handlers and the
            // fullobby:// "open with" picker fills up with stale duplicate Fullobby entries.
            // Prune every Fullobby handler that isn't the running exe so exactly one remains.
            try
            {
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
                Log.Warning(ex, "Pruning stale fullobby:// handlers failed");
            }
        });

        // Surface the efficiency-mode crash-recovery notice now that the toast host exists.
        var toastService = AppHost.Services.GetRequiredService<Services.InAppToastService>();
        var notice = backup.TakeStartupRestoreNotice();
        if (notice is not null)
        {
            toastService.Info(notice);
        }

        // Credential protection failing is handled by MainWindow, which owns the XamlRoot the modal
        // needs — see MainWindow.ShowSecretsUnprotectedDialogAsync.

        // The first instance itself may have been protocol-launched or scheduled-task-launched
        // (fullobby:// deep link, or --autoseed from a scheduled task).
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());

        // Fallback for a fresh scheduled-task launch where the activation args don't carry the flag.
        if (HasAutoseedArg(Environment.GetCommandLineArgs()))
        {
            StartAutoseed(WakeKeyFromArgs(Environment.GetCommandLineArgs()));
        }
    }

    /// <summary>Missed-autoseed watchdog fired (off-thread) with the wake's "HH:MM" UTC key —
    /// marshal onto the UI and run it.</summary>
    private void OnAutoseedDue(string wakeKey) => StartAutoseed(wakeKey);

    /// <summary>Ready checks are waiting on this operator — toast them. Answering happens on the
    /// Admin tab's hub, which the toast click brings the window forward for; the notice itself is
    /// the whole job here, because being told is the part that was missing.</summary>
    private void OnReadyChecksDue(IReadOnlyList<ReadyCheckNotice> checks)
    {
        try
        {
            var toasts = AppHost.Services.GetRequiredService<Services.ToastService>();
            var first = checks[0];
            var minutes = Math.Max(0, first.SecondsUntilWindow / 60);
            var body = checks.Count == 1
                ? $"{first.ServerName} seeds in {minutes} min and needs a ready confirmation. "
                  + "Open Fullobby to confirm — unconfirmed servers lose their seed priority."
                : $"{checks.Count} servers need a ready confirmation, the first in {minutes} min. "
                  + "Open Fullobby to confirm.";
            toasts.PlayAttentionSound();
            toasts.Show("Ready check", body);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show ready-check notification");
        }
    }

    /// <summary>
    /// An auto-seed was refused because onboarding isn't done. If the task that woke us is a leftover
    /// from a previous install, remove it — otherwise the machine keeps waking daily for a seed that
    /// will always be refused, and the fixed uninstaller can't reach installs already in the wild.
    /// The removal is awaited before the launch ends: the 4s shutdown watchdog would otherwise race
    /// the schtasks call and the orphan would survive every wake.
    /// </summary>
    private async Task HandleBlockedAutoseedAsync()
    {
        // Let session restore settle before judging. It heals a completion flag lost to a crash and
        // arms the network gate, so the flag read a moment ago can still be wrong in both
        // directions — and deleting a real user's task would silently end their auto-seed. Bounded,
        // so a hung network call can't strand a process that exists only to seed.
        try
        {
            await _sessionRestore.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Session restore didn't settle before the auto-seed orphan check");
        }

        // Re-reads the persisted flag itself, so a restore that just healed it keeps the tasks.
        try
        {
            await AppHost.Services.GetRequiredService<AutoSeedService>()
                .RemoveOrphanedTasksAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Orphaned auto-seed task cleanup failed");
        }

        EndAutoseedLaunch("onboarding hasn't been completed");
    }

    /// <summary>The auto-seed re-armed its wake for a moved window — the scheduled task will wake the
    /// machine again at the new time, so this process has nothing left to do.</summary>
    private void OnResleepRequested() =>
        EndAutoseedLaunch("re-armed for a moved window; letting the PC sleep");

    /// <summary>The auto-seed was refused or abandoned before it seeded anything (raised by the view
    /// model with the reason).</summary>
    private void OnAutoseedAbandoned(string reason) => EndAutoseedLaunch(reason);

    /// <summary>
    /// The auto-seed this process was started for is over without a seed to keep it alive. Release
    /// keep-awake and shut down so the machine can go back to sleep. No-op in an interactive
    /// session — the user opened the app themselves, so it stays open.
    ///
    /// Quits through <see cref="MainWindow.ForceQuit"/>, not <c>Application.Exit()</c>: with
    /// close-to-tray enabled (the default) <c>OnAppWindowClosing</c> cancels the close and merely
    /// hides the window, so Exit() leaves the process alive in the tray — holding the machine awake
    /// after a 3 a.m. wake instead of releasing it. ForceQuit sets the flag that close handler
    /// checks, so the close goes through to Window.Closed and the real shutdown path.
    ///
    /// Safe to call from any thread.
    /// </summary>
    private void EndAutoseedLaunch(string reason)
    {
        if (!_launchedForAutoseed)
        {
            return; // interactive session — leave the app running
        }

        Log.Information("Ending the auto-seed launch: {Reason}", reason);
        try { AppHost.Services.GetRequiredService<Core.Native.KeepAwake>().Release(); }
        catch (Exception ex) { Log.Warning(ex, "Keep-awake release before exit failed"); }

        var window = _window;
        if (window is null)
        {
            Exit(); // no window ever came up — nothing to cancel the close
            return;
        }
        window.DispatcherQueue.TryEnqueue(window.ForceQuit);
    }

    /// <summary>Marshal to the UI thread and kick off the auto-seed countdown.
    /// <paramref name="wakeKey"/> identifies which wake fired ("HH:MM" UTC; null for a legacy
    /// wake-less launch) so the run is recorded against the right wake.</summary>
    private void StartAutoseed(string? wakeKey)
    {
        Log.Information("Auto-seed requested (wake {Wake})", wakeKey ?? "unspecified");

        // Refuse before touching the window. The scheduled task is registered with Windows and
        // survives an uninstall or a wiped config, so it can fire at a machine that is sitting at
        // first-run onboarding; the same is true after a session is lost or a network membership
        // goes away. The view model refuses too — this copy exists so a blocked request doesn't
        // first drag the window in front of the user (potentially waking the machine at the seed
        // hour to do it).
        var account = AppHost.Services.GetRequiredService<ViewModels.AccountViewModel>();
        if (account.SeedingBlocked)
        {
            Log.Information("Auto-seed request ignored — the account isn't ready to seed");
            _ = HandleBlockedAutoseedAsync();
            return;
        }

        var vm = AppHost.Services.GetRequiredService<ViewModels.SeedingViewModel>();
        if (!_autoseedEventsWired)
        {
            vm.ResleepRequested += OnResleepRequested;
            vm.AutoseedAbandoned += OnAutoseedAbandoned;
            _autoseedEventsWired = true;
        }
        var queue = _window?.DispatcherQueue;
        if (queue is null)
        {
            _ = vm.RunAutoseedAsync(wakeKey);
            return;
        }
        queue.TryEnqueue(() =>
        {
            _window?.BringToFront();
            _ = vm.RunAutoseedAsync(wakeKey);
        });
    }

    /// <summary>Whether any token asks for a tray-only start (--minimized).</summary>
    private static bool HasMinimizedArg(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, Branding.MinimizedArg, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether any token requests an auto-seed launch (--autoseed-HHMM, --autoseed, plus
    /// legacy --autoseed-*/--seed-*).</summary>
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

    /// <summary>The fired wake's "HH:MM" UTC key from a --autoseed-HHMM token, or null for the
    /// legacy wake-less forms.</summary>
    private static string? WakeKeyFromArgs(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (AutoSeedSlot.TryParseWakeArg(arg, out var t))
            {
                return $"{t.Hour:D2}:{t.Minute:D2}";
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
        // A scheduled task launching a second instance forwards its --autoseed flag here.
        if (args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs autoseedLaunch
            && autoseedLaunch.Arguments is { Length: > 0 } rawArgs
            && rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { } tokens
            && HasAutoseedArg(tokens))
        {
            StartAutoseed(WakeKeyFromArgs(tokens));
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
                _ = account.HandleLinkCallbackAsync(link.Provider, link.StagedCode);
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

        // A bare HostBuilder, not Host.CreateDefaultBuilder(). The default builder registers
        // appsettings.json + appsettings.{Environment}.json providers with reloadOnChange: true,
        // which puts a FileSystemWatcher on the app directory — two OS change-notification handles
        // and a background thread that exist to reload files this project does not have and never
        // reads (nothing here injects IConfiguration). It also registers Console/Debug/EventSource
        // logging providers that UseSerilog then makes moot.
        return new HostBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                // Explicit because the bare builder adds no logging of its own: UseSerilog supplies
                // the ILoggerFactory, but the open-generic ILogger<T> every service injects comes
                // from AddLogging. AddHttpClient happens to call it too — this does not rely on that.
                // Idempotent (TryAdd-based), so the duplicate call costs nothing.
                services.AddLogging();

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
