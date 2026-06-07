using ChllSeeder.Core;
using ChllSeeder.Core.Activation;
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

        _window = AppHost.Services.GetRequiredService<MainWindow>();
        _window.Closed += (_, _) =>
        {
            // Close-to-tray arrives in Phase 2; for now closing the window exits.
            Log.Information("Main window closed, shutting down");
            AppHost.StopAsync().GetAwaiter().GetResult();
            Log.CloseAndFlush();
        };
        _window.Activate();

        // The first instance itself may have been protocol-launched
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
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

    private static void HandleActivation(AppActivationArguments args)
    {
        var uri = ExtractDeepLinkUri(args);
        if (uri is null)
        {
            return; // plain launch, nothing to do
        }

        // Phase 0: prove the deep-link pipeline; OAuth wiring lands in Phase 3.
        // Never log token values.
        switch (DeepLinkParser.Parse(uri))
        {
            case DeepLinkAction.AuthCallback:
                Log.Information("Deep link: auth callback received");
                break;
            case DeepLinkAction.LinkCallback link:
                Log.Information("Deep link: link callback for provider {Provider}", link.Provider);
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
                services.AddSingleton<MainWindow>();
                services.AddSingleton<ViewModels.ShellViewModel>();
            })
            .Build();
    }
}
