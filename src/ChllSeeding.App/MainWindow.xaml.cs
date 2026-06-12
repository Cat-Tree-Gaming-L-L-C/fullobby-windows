using System.Diagnostics;
using ChllSeeding.App.Services;
using ChllSeeding.App.ViewModels;
using ChllSeeding.Core;
using ChllSeeding.Core.Config;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace ChllSeeding.App;

public sealed partial class MainWindow : Window
{
    // Same footprint as the Rust app (500x520 content + titlebar/nav chrome)
    private const double LogicalWidth = 500;
    private const double LogicalHeight = 600;

    private readonly ConfigService _config;
    private readonly InAppToastService _toasts;

    /// <summary>Account/onboarding view model — bound by the onboarding overlay in the shell.</summary>
    public AccountViewModel Account { get; }

    // Set when the user really wants to exit (tray Quit/Restart) so the close
    // handler stops minimizing to tray and lets the window close.
    private bool _forceQuit;

    public MainWindow(ConfigService config, InAppToastService toasts, AccountViewModel account)
    {
        _config = config;
        _toasts = toasts;
        Account = account;

        InitializeComponent();

        Title = Branding.ProductName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // Frameless window: extend content into the titlebar, custom drag region
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // Size in logical units once the XAML root (and its DPI scale) exists
        RootGrid.Loaded += (_, _) =>
        {
            var scale = RootGrid.XamlRoot.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(LogicalWidth * scale),
                (int)(LogicalHeight * scale)));
        };

        // Restore the saved theme (light/dark); absent → follow the system default.
        ApplyTheme(_config.GetString("theme"));

        NavView.SelectedItem = SeedNavItem;

        // Tray: left-click restores the window; ForceCreate so the icon exists
        // even while the window is hidden to tray.
        TrayIcon.LeftClickCommand = new RelayCommand(BringToFront);
        TrayIcon.ForceCreate();
        Closed += OnWindowClosed;

        // Close-to-tray (default on): intercept the X and hide instead of exiting.
        AppWindow.Closing += OnAppWindowClosing;

        // In-app toast stack.
        ToastHost.ItemsSource = _toasts.Toasts;

        // First-run onboarding overlay: shown until completed/skipped (x:Bind isn't available
        // on a Window root, so drive visibility from the VM here).
        UpdateOnboardingVisibility();
        Account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountViewModel.ShowOnboarding))
            {
                UpdateOnboardingVisibility();
            }
        };
    }

    private void UpdateOnboardingVisibility() =>
        Onboarding.Visibility = Account.ShowOnboarding ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set the app theme (dark/light) on the window root and persist it.
    /// Mirrors the Rust data-theme toggle in Settings.</summary>
    public void SetTheme(bool dark)
    {
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _config.SetString("theme", dark ? "dark" : "light");
    }

    /// <summary>True when the saved theme is dark (used to seed the Settings toggle).</summary>
    public bool IsDarkTheme => RootGrid.RequestedTheme == ElementTheme.Dark;

    private void ApplyTheme(string? theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Restore and foreground the window (deep-link / repeat-launch / tray activation).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        Activate();
    }

    // ── Close-to-tray / quit / restart ──────────────────────────────────────

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Minimize to tray unless the user asked to exit or disabled the setting.
        if (!_forceQuit && _config.GetBool("close_to_tray", true))
        {
            args.Cancel = true;
            AppWindow.Hide();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Dispose the tray icon so it doesn't ghost in the notification area.
        TrayIcon.Dispose();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e) => BringToFront();

    private void TrayQuit_Click(object sender, RoutedEventArgs e)
    {
        _forceQuit = true;
        Close(); // not cancelled by OnAppWindowClosing → Window.Closed → App shutdown
    }

    private void TrayRestart_Click(object sender, RoutedEventArgs e)
    {
        // Spawn a delayed re-launch (2s) so single-instance registration is released
        // before the new process starts, then exit. Port of platform::restart::restart_app.
        try
        {
            _config.FlushPendingSaves();
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul && \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to spawn delayed restart");
        }

        _forceQuit = true;
        Close();
    }

    private void Toast_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is InAppToast toast)
        {
            _toasts.Dismiss(toast);
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(Views.SettingsPage));
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var pageType = tag switch
        {
            "seed" => typeof(Views.SeedPage),
            "launch" => typeof(Views.LaunchPage),
            "leaderboard" => typeof(Views.LeaderboardPage),
            "tools" => typeof(Views.ToolsPage),
            _ => null,
        };
        if (pageType is not null)
        {
            NavigateTo(pageType);
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
