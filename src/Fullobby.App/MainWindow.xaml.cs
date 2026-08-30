using System.Diagnostics;
using System.Globalization;
using Fullobby.App.Services;
using Fullobby.App.ViewModels;
using Fullobby.Core;
using Fullobby.Core.Config;
using Fullobby.Core.Seeding;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.UI;

namespace Fullobby.App;

public sealed partial class MainWindow : Window
{
    // Window footprint (500x520 content + titlebar/nav chrome). Also the floor the window can be
    // dragged to: the shell was laid out against these numbers, so they double as the resize
    // minimum rather than letting the nav rail and seed board clip.
    private const double LogicalWidth = 500;
    private const double LogicalHeight = 600;

    /// <summary>Persisted window size as a single <c>"WxH"</c> value in logical units — logical so
    /// the window keeps its physical size across monitors of different DPI, and *one* key because
    /// ConfigService coalesces writes on a debounce: a width and a height written as two keys land
    /// on disk at different times, so a hide-to-tray or a crash between them leaves a mismatched
    /// pair that restores the window to a shape the user never chose. One key can't tear.
    /// Single call site, so it stays a literal here (see <see cref="ConfigKeys"/>).</summary>
    private const string SizeKey = "window_size";

    /// <summary>Set while <see cref="RootGrid"/>'s Loaded handler applies the restored size, so the
    /// resulting AppWindow.Changed doesn't immediately write back what we just read.</summary>
    private bool _applyingRestoredSize;

    private readonly ConfigService _config;
    private readonly InAppToastService _toasts;

    /// <summary>Account/onboarding view model — bound by the onboarding overlay in the shell.</summary>
    public AccountViewModel Account { get; }

    // Set when the user really wants to exit (tray Quit/Restart) so the close
    // handler stops minimizing to tray and lets the window close.
    private bool _forceQuit;

    /// <summary>One-shot guard for the credentials-unprotected modal. The event behind it can fire
    /// on every failed secret write, and ContentDialog throws if a second one opens while the
    /// first is still up.</summary>
    private bool _secretsDialogShown;

    public MainWindow(ConfigService config, InAppToastService toasts, AccountViewModel account)
    {
        _config = config;
        _toasts = toasts;
        Account = account;

        InitializeComponent();

        Title = Branding.ProductName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // Titlebar version string + a DEV BUILD badge shown only in debug builds.
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
#if DEBUG
        DevBadge.Visibility = Visibility.Visible;
#endif

        // Frameless window: extend content into the titlebar, custom drag region
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        // Native Mica material as the window base. The brand surfaces sit on top as
        // solid layers; the deep, slightly translucent backdrop gives the dark
        // palette a premium native feel. Windows 11 is the only supported platform
        // (decision 2026-08-12), and it always supports Mica — no fallback.
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };

        // Size in logical units once the XAML root (and its DPI scale) exists. A "Start with
        // Windows" launch never activates the window, so this does not run at startup — it runs
        // when the tray first shows the window. Everything size-related therefore lives here, not
        // in the constructor, or a tray start would come up unsized and unconstrained.
        RootGrid.Loaded += (_, _) =>
        {
            var scale = RootGrid.XamlRoot.RasterizationScale;

            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.PreferredMinimumWidth = (int)(LogicalWidth * scale);
                p.PreferredMinimumHeight = (int)(LogicalHeight * scale);
            }

            // Restore the last size the user chose, never below the design floor — a config written
            // by hand, or carried over from a build with a larger minimum, must not be able to pin
            // the window smaller than the layout supports.
            var (width, height) = ParseSize(_config.GetString(SizeKey));

            _applyingRestoredSize = true;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(width * scale),
                (int)(height * scale)));
            _applyingRestoredSize = false;

            // Only now is there a XamlRoot to host a dialog. ConfigService is constructed long
            // before any window exists, so a protection failure raised during startup is read off
            // the flag here rather than through the event below.
            if (_config.HasUnprotectedSecrets)
            {
                _ = ShowSecretsUnprotectedDialogAsync();
            }
        };

        // Later failures (a secret that fails to encrypt mid-session) arrive by event.
        _config.SecretProtectionUnavailable += OnSecretProtectionUnavailable;

        // Restore the saved theme (light / dark / system); absent → the brand default, dark.
        ApplyTheme(_config.GetString("theme"));

        // While following the system setting, the OS can flip the theme under us. The caption
        // buttons are drawn by the shell and don't inherit the XAML theme, so re-tint them.
        RootGrid.ActualThemeChanged += (_, _) => UpdateTitleBarColors();

        NavView.SelectedItem = SeedNavItem;

        // Tray: left-click restores the window; ForceCreate so the icon exists
        // even while the window is hidden to tray.
        // The context-menu items MUST use Command, not Click — the native Win32
        // PopupMenu H.NotifyIcon renders swallows XAML Click events (issue #109),
        // so wire each item's command here.
        TrayIcon.LeftClickCommand = new RelayCommand(BringToFront);
        TrayShowItem.Command = new RelayCommand(BringToFront);
        TrayRestartItem.Command = new RelayCommand(RestartApp);
        TrayQuitItem.Command = new RelayCommand(Quit);
        TrayIcon.ForceCreate();
        Closed += OnWindowClosed;

        // Close-to-tray (default on): intercept the X and hide instead of exiting.
        AppWindow.Closing += OnAppWindowClosing;

        // Remember the size the user drags to. ConfigService coalesces writes on a 500ms debounce,
        // so firing this for every frame of a drag-resize costs one write at the end of it.
        AppWindow.Changed += OnAppWindowSizeChanged;

        // In-app toast stack.
        ToastHost.ItemsSource = _toasts.Toasts;

        // First-run onboarding overlay: shown until completed/skipped (x:Bind isn't available
        // on a Window root, so drive visibility from the VM here).
        UpdateOnboardingVisibility();
        UpdateManageVisibility();
        UpdateNetworkGateBar();
        Account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountViewModel.ShowOnboarding))
            {
                UpdateOnboardingVisibility();
                // The banner is suppressed behind the overlay, so it has to be re-evaluated
                // when the overlay closes — otherwise a gate armed during onboarding stays down.
                UpdateNetworkGateBar();
            }
            else if (e.PropertyName == nameof(AccountViewModel.CanReachManagePanel))
            {
                UpdateManageVisibility();
            }
            else if (e.PropertyName == nameof(AccountViewModel.NetworkGateActive))
            {
                UpdateNetworkGateBar();
            }
        };
    }

    private void UpdateOnboardingVisibility() =>
        Onboarding.Visibility = Account.ShowOnboarding ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raise or lower the join-a-network banner. Held down while the onboarding overlay is
    /// up: first run has its own network step, and a banner stacked behind an opaque overlay would
    /// only appear from nowhere the moment that overlay closed.</summary>
    private void UpdateNetworkGateBar() =>
        NetworkGateBar.IsOpen = Account.NetworkGateActive && !Account.ShowOnboarding;

    /// <summary>Open the shared join dialog from the banner — the same one Settings uses.</summary>
    private async void NetworkGateJoin_Click(object sender, RoutedEventArgs e) =>
        await Views.NetworkJoinDialog.ShowAsync(Account, RootGrid.XamlRoot);

    /// <summary>Raised by ConfigService, possibly off the UI thread.</summary>
    private void OnSecretProtectionUnavailable() =>
        DispatcherQueue.TryEnqueue(() => _ = ShowSecretsUnprotectedDialogAsync());

    /// <summary>
    /// Tell the user their credentials cannot be saved on this PC.
    ///
    /// Modal rather than a toast because of when it lands: a ContentDialog renders above the
    /// onboarding overlay, so on first run it has to be acknowledged before the user can reach the
    /// sign-in buttons — and the whole point of the message is that the sign-in they are about to
    /// do will not survive closing the app. As a toast this was the one warning guaranteed to be
    /// missed, since it was covered by the overlay for the entire time it was relevant.
    ///
    /// Shown at most once per session (see <see cref="_secretsDialogShown"/>).
    /// </summary>
    private async Task ShowSecretsUnprotectedDialogAsync()
    {
        // No XamlRoot yet: the RootGrid.Loaded handler re-reads HasUnprotectedSecrets and shows it
        // then, so an early event is deferred rather than dropped.
        if (_secretsDialogShown || RootGrid.XamlRoot is null)
        {
            return;
        }
        _secretsDialogShown = true;

        var dialog = new ContentDialog
        {
            Title = "Credentials can't be saved",
            Content = "Windows couldn't encrypt your saved credentials on this PC, so they haven't "
                      + "been saved to disk. You'll stay signed in until you close Fullobby, then "
                      + "you'll need to sign in again.",
            CloseButtonText = "Continue",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception e)
        {
            // Another dialog can already be open (a page's confirm prompt). Losing the warning is
            // bad; taking the app down over it is worse.
            Log.Warning(e, "Could not show the credential-protection dialog");
        }
    }

    /// <summary>Show the Manage tab while /me reports the user can reach any panel surface —
    /// a global grant, community Admin, network Admin, or an <b>operator</b>. If that
    /// disappears (sign-out, grant revoked) while the tab is open, bounce to Seed.
    /// <para>Widened twice: it gated on a <i>global</i> grant until 0.4.1, hiding it from
    /// every community admin, then on the manage flags, which still hid it from operators
    /// — who answer ready checks and toggle servers, both of which the panel serves.</para>
    /// </summary>
    private void UpdateManageVisibility()
    {
        var reachable = Account.CanReachManagePanel;
        ManageNavItem.Visibility = reachable ? Visibility.Visible : Visibility.Collapsed;
        if (!reachable && ContentFrame.CurrentSourcePageType == typeof(Views.AdminPage))
        {
            NavView.SelectedItem = SeedNavItem;
        }
    }

    /// <summary>Set the app theme on the window root and persist it.
    /// <see cref="ElementTheme.Default"/> means "follow the system setting".</summary>
    public void SetTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
        _config.SetString("theme", theme switch
        {
            ElementTheme.Light => "light",
            ElementTheme.Dark => "dark",
            _ => "system",
        });
        UpdateTitleBarColors();
    }

    /// <summary>The configured theme, including <see cref="ElementTheme.Default"/> for
    /// follow-the-system (used to seed the Settings picker).</summary>
    public ElementTheme CurrentTheme => RootGrid.RequestedTheme;

    /// <summary>True when what's actually on screen is dark. Reads <c>ActualTheme</c>, not
    /// <c>RequestedTheme</c>, so it stays correct while following the system setting.</summary>
    public bool IsDarkTheme => RootGrid.ActualTheme != ElementTheme.Light;

    private void ApplyTheme(string? theme)
    {
        // Dark is the brand default: absent or unrecognised resolves to dark, so an existing
        // install's appearance is unchanged on upgrade. Only "system" defers to the OS.
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        UpdateTitleBarColors();
    }

    /// <summary>Tint the system caption buttons (min/close) to match the active theme.
    /// They're drawn by the shell, so they don't inherit the XAML theme automatically.</summary>
    private void UpdateTitleBarColors()
    {
        var tb = AppWindow.TitleBar;
        bool dark = IsDarkTheme;

        var fg = dark ? Color.FromArgb(0xFF, 0xE8, 0xE9, 0xEB) : Color.FromArgb(0xFF, 0x1B, 0x1E, 0x24);
        var inactiveFg = dark ? Color.FromArgb(0xFF, 0x8B, 0x91, 0x9A) : Color.FromArgb(0xFF, 0x5C, 0x63, 0x6E);
        var hover = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        var pressed = dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00);

        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = hover;
        tb.ButtonPressedBackgroundColor = pressed;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = inactiveFg;
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

    /// <summary>Persist the window size after a resize. Only the restored state is recorded: saving
    /// while maximized would store the screen-sized bounds as the size to come back to, so
    /// un-maximizing (or the next launch) would have nothing smaller to restore to, and minimized
    /// reports a size that isn't one the user chose.</summary>
    private void OnAppWindowSizeChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _applyingRestoredSize)
        {
            return;
        }
        if (sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
        {
            return;
        }

        // Null before the first Loaded; the size we'd record then isn't the user's anyway.
        if (RootGrid.XamlRoot is not { RasterizationScale: > 0 } root)
        {
            return;
        }

        var width = (int)Math.Round(sender.Size.Width / root.RasterizationScale);
        var height = (int)Math.Round(sender.Size.Height / root.RasterizationScale);
        _config.SetString(SizeKey, string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"));
    }

    /// <summary>Parse a stored <c>"WxH"</c> size, clamped to the design floor. Anything unparseable
    /// — absent, hand-edited, or written by a future build in another format — falls back to the
    /// default footprint rather than throwing on a path that runs before the window is ever shown.</summary>
    private static (double Width, double Height) ParseSize(string? stored)
    {
        if (stored is not null)
        {
            // Invariant on both ends: the value is written as plain integers, and a config file that
            // travels to a comma-decimal locale must still read back as the same size.
            var parts = stored.Split('x', 2);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            {
                return (Math.Max(w, LogicalWidth), Math.Max(h, LogicalHeight));
            }
        }
        return (LogicalWidth, LogicalHeight);
    }

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

    /// <summary>Quit the app through the normal shutdown path (no close-to-tray). Used by the
    /// self-updater after launching the installer so it can replace the running exe, and by
    /// <c>App.EndAutoseedLaunch</c> to shut down a process that exists only to run an auto-seed.
    /// Callers must use this rather than <c>Application.Exit()</c>, which
    /// <see cref="OnAppWindowClosing"/> turns into a hide whenever close-to-tray is on.</summary>
    public void ForceQuit()
    {
        _forceQuit = true;
        Close();
    }

    /// <summary>Fully exit the app (tray "Quit"). Sets the force-quit flag so the close handler
    /// doesn't minimize to tray, then closes → Window.Closed → App shutdown → process exit.</summary>
    private void Quit()
    {
        _forceQuit = true;
        Close(); // not cancelled by OnAppWindowClosing → Window.Closed → App shutdown
    }

    private void RestartApp()
    {
        // Spawn a delayed re-launch (2s) so single-instance registration is released
        // before the new process starts, then exit. Port of platform::restart::restart_app.
        try
        {
            _config.FlushPendingSaves();

            // End any open seeding session BEFORE relaunch — so
            // the new instance doesn't briefly overlap an old, still-open session. The window-close
            // path also stops it, but doing it here gives the stop the full 2s relaunch delay to land.
            try
            {
                App.AppHost.Services.GetRequiredService<HeartbeatService>().StopFireAndForget("app_restart");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Heartbeat stop on restart failed");
            }

            var exe = Environment.ProcessPath;
            // This is the one place left that builds a shell command string, because the 2s delay
            // needs an intermediary that outlives this process. `exe` comes from the OS and Windows
            // paths cannot contain a quote — which is exactly why the interpolation below is safe
            // today. Enforce that rather than leaving it as an unwritten assumption: if the path
            // ever could contain a quote or newline, the command would break out of its quoting.
            if (exe is not null && exe.AsSpan().IndexOfAny('"', '\r', '\n') >= 0)
            {
                Log.Error("Executable path contains unexpected characters — skipping delayed restart");
                exe = null;
            }
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

        // Hard-exit safety net (port of restart_app's sleep(3) + process::exit(0)): if the normal
        // close/host-shutdown path hangs, force the process down so the relaunched instance — which
        // starts after the 2s timeout — doesn't redirect back into this dying one. No-op if we
        // already exited cleanly (this task dies with the process).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Log.Warning("Restart: clean shutdown didn't exit in time, forcing process exit");
            Environment.Exit(0);
        });
    }

    /// <summary>Shut the app down so a freshly launched installer can replace the running exe.
    /// Mirrors the restart shutdown sequence (flush config + stop heartbeat + force-close + hard-exit
    /// safety net) but without the delayed relaunch — the installer owns the relaunch. Port of the
    /// shutdown half of <c>updater::launch_installer</c> (flush + stop_heartbeat_sync + close_app).</summary>
    public void QuitForUpdate()
    {
        try
        {
            _config.FlushPendingSaves();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Config flush before update exit failed");
        }

        try
        {
            App.AppHost.Services.GetRequiredService<HeartbeatService>().StopFireAndForget("update");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Heartbeat stop before update exit failed");
        }

        _forceQuit = true;
        Close();

        // Hard-exit safety net (mirrors the restart path): if the clean shutdown hangs, force the
        // process down so it isn't holding the exe open when the installer tries to overwrite it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Log.Warning("Update: clean shutdown didn't exit in time, forcing process exit");
            Environment.Exit(0);
        });
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
            "about" => typeof(Views.AboutPage),
            "admin" => typeof(Views.AdminPage),
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
