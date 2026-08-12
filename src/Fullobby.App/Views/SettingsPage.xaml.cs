using Fullobby.App.ViewModels;
using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Fullobby.Core.Platform;
using Fullobby.Core.Scheduling;
using Fullobby.Core.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fullobby.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ConfigService _config;
    private readonly MainWindow _window;
    private readonly StartupRegistry _startup;
    private readonly AutoSeedService _autoseed;
    private readonly UpdaterService _updater;

    public AccountViewModel Account { get; }

    // Suppresses the Toggled/SelectionChanged handlers while seeding controls from config.
    private bool _loading;

    // Guards the auto-seed buttons: both handlers shell out to schtasks and end in a dialog.
    private bool _autoseedBusy;

    public SettingsPage()
    {
        _config = App.AppHost.Services.GetRequiredService<ConfigService>();
        _window = App.AppHost.Services.GetRequiredService<MainWindow>();
        _startup = App.AppHost.Services.GetRequiredService<StartupRegistry>();
        _autoseed = App.AppHost.Services.GetRequiredService<AutoSeedService>();
        _updater = App.AppHost.Services.GetRequiredService<UpdaterService>();
        Account = App.AppHost.Services.GetRequiredService<AccountViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SeedControls();
        // Refresh the networks list in the background (non-fatal; the last list stays shown).
        if (Account.IsLoggedIn)
        {
            _ = Account.RefreshNetworksAsync();
        }
    }

    /// <summary>Initialize every control from current config / VM state without firing handlers.</summary>
    private void SeedControls()
    {
        _loading = true;
        ThemeCombo.SelectedIndex = _window.CurrentTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Default => 2, // follow system
            _ => 0,                    // dark (brand default)
        };
        CloseToTrayToggle.IsOn = _config.GetBool("close_to_tray", true);
        SwitchNotificationToggle.IsOn = _config.GetBool("switch_notification", true);
        LeaderboardToggle.IsOn = Account.ShowOnLeaderboard;

        var duration = _config.GetString("splash_bypass_duration") ?? "20";
        SplashDurationCombo.SelectedIndex = duration switch
        {
            "10" => 0,
            "40" => 2,
            "60" => 3,
            _ => 1, // 20s default
        };

        StartupToggle.IsOn = _startup.IsEnabled();
        BetaToggle.IsOn = _config.GetString("update_channel") == "beta";
        _loading = false;

        // Auto-seed task status comes from schtasks — load it off the UI thread.
        _ = LoadAutoseedStatusAsync();
    }

    // ── Automation: Start with Windows ──────────────────────────────────────

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        try
        {
            if (StartupToggle.IsOn)
            {
                _startup.Enable();
            }
            else
            {
                _startup.Disable();
            }
        }
        catch (Exception)
        {
            _loading = true;
            StartupToggle.IsOn = !StartupToggle.IsOn; // revert on failure
            _loading = false;
            await AlertAsync("Error", "Couldn't update the Start with Windows setting.");
        }
    }

    // ── Automation: Auto-Seed scheduling ────────────────────────────────────

    private async Task LoadAutoseedStatusAsync()
    {
        try
        {
            var s = await _autoseed.GetStatusAsync();
            if (s.Installed)
            {
                var text = s.UtcTime is not null ? $"Daily at {s.UtcTime} UTC" : "Scheduled";
                if (!string.IsNullOrEmpty(s.NextRun))
                {
                    text += $" · next: {s.NextRun}";
                }
                AutoseedStatus.Text = text;
                AutoseedSetup.Content = "Change";
                AutoseedRemove.Visibility = Visibility.Visible;
            }
            else
            {
                AutoseedStatus.Text = "Not scheduled";
                AutoseedSetup.Content = "Set up";
                AutoseedRemove.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            AutoseedStatus.Text = "Status unavailable";
        }
    }

    private void AutoseedSetup_Click(object sender, RoutedEventArgs e) => _ = SetupAutoseedAsync();

    private void AutoseedRemove_Click(object sender, RoutedEventArgs e) => _ = RemoveAutoseedAsync();

    /// <summary>Disable both auto-seed buttons and show the spinner while a schtasks call is in
    /// flight. Each call shells out to schtasks.exe and ends in a dialog, so without this a
    /// double-click runs the operation twice and stacks two dialogs.</summary>
    private bool BeginAutoseedBusy()
    {
        if (_autoseedBusy)
        {
            return false;
        }
        _autoseedBusy = true;
        AutoseedSpinner.Visibility = Visibility.Visible;
        AutoseedSetup.IsEnabled = false;
        AutoseedRemove.IsEnabled = false;
        return true;
    }

    private void EndAutoseedBusy()
    {
        _autoseedBusy = false;
        AutoseedSpinner.Visibility = Visibility.Collapsed;
        AutoseedSetup.IsEnabled = true;
        AutoseedRemove.IsEnabled = true;
    }

    private async Task SetupAutoseedAsync()
    {
        if (!BeginAutoseedBusy())
        {
            return;
        }
        try
        {
            var message = await _autoseed.SetupAsync();
            await LoadAutoseedStatusAsync();
            await AlertAsync("Auto-Seed Setup", message);
        }
        catch (Exception)
        {
            await AlertAsync("Error", "Failed to set up the auto-seed task. Check the logs for details.");
        }
        finally
        {
            EndAutoseedBusy();
        }
    }

    private async Task RemoveAutoseedAsync()
    {
        // Removing the task silently stops a scheduled wake the user is relying on, so confirm it.
        var confirm = new ContentDialog
        {
            Title = "Remove Auto-Seed",
            Content = "Your PC will no longer wake to seed automatically. You can set this up again at any time.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!BeginAutoseedBusy())
        {
            return;
        }
        var removed = true;
        try
        {
            await _autoseed.UninstallAsync();
        }
        catch (Exception)
        {
            // A failed removal leaves a task that will still wake the machine — the user has to be
            // told, otherwise the refreshed status ("Scheduled") reads as the removal not applying yet.
            removed = false;
        }
        finally
        {
            await LoadAutoseedStatusAsync();
            EndAutoseedBusy();
        }

        if (!removed)
        {
            await AlertAsync("Error",
                "Couldn't remove the auto-seed task, so your PC may still wake to seed. Check the logs for details.");
        }
    }

    private Task AlertAsync(string title, string content) =>
        new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        }.ShowAsync().AsTask();

    // ── Account actions ─────────────────────────────────────────────────────

    private async void ChangeName_Click(object sender, RoutedEventArgs e)
    {
        // Guests can only randomize an anonymous name; OAuth users type one.
        if (Account.IsGuest)
        {
            await Account.RandomizeNameAsync();
            return;
        }

        var input = new TextBox
        {
            PlaceholderText = "Enter nickname…",
            Text = Account.DisplayName,
            MaxLength = 32,
        };
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush)
            && brush is Microsoft.UI.Xaml.Media.Brush critical)
        {
            errorText.Foreground = critical;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(input);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Change Name",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        // Validate before closing: an empty or rejected name would otherwise dismiss the dialog and
        // leave the name unchanged, with the reason (if any) landing in a toast the user has already
        // looked away from. Same deferral + inline-error shape as the join-network dialog below.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var trimmed = input.Text.Trim();
            var error = trimmed.Length == 0
                ? "Enter a nickname."
                : ApiValidation.ValidateDisplayName(trimmed);
            if (error is not null)
            {
                args.Cancel = true;
                errorText.Text = error;
                errorText.Visibility = Visibility.Visible;
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Account.UpdateDisplayNameAsync(input.Text);
        }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete Account",
            Content = "This will permanently delete your account and all seeding history. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Account.DeleteAccountCommand.ExecuteAsync(null);
        }
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string provider)
        {
            // Through the generated command, not the method: AsyncRelayCommand refuses concurrent
            // executions, so a double-click can't fire two unlink requests. Calling the method
            // directly bypasses that guard, which is the whole reason the command exists.
            await Account.UnlinkProviderCommand.ExecuteAsync(provider);
        }
    }

    private async void LeaderboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        LeaderboardSpinner.Visibility = Visibility.Visible;
        LeaderboardToggle.IsEnabled = false;
        try
        {
            await Account.SetShowOnLeaderboardAsync(LeaderboardToggle.IsOn);
        }
        finally
        {
            // Re-seed from VM state so a failed update reverts the toggle (the API call leaves
            // User unchanged on error).
            _loading = true;
            LeaderboardToggle.IsOn = Account.ShowOnLeaderboard;
            _loading = false;
            LeaderboardToggle.IsEnabled = true;
            LeaderboardSpinner.Visibility = Visibility.Collapsed;
        }
    }

    // ── Seeding networks ────────────────────────────────────────────────────

    /// <summary>Serializes the network-row actions. Reorder and Leave all rewrite the same
    /// priority list and push the whole order to the API, so two overlapping calls can race and
    /// persist an order the user never asked for. These have no generated command to lean on
    /// (MoveNetworkAsync takes two arguments), hence an explicit gate.</summary>
    private bool _networkBusy;

    private async void NetworkUp_Click(object sender, RoutedEventArgs e) => await MoveNetworkAsync(sender, -1);

    private async void NetworkDown_Click(object sender, RoutedEventArgs e) => await MoveNetworkAsync(sender, +1);

    private async Task MoveNetworkAsync(object sender, int delta)
    {
        if (_networkBusy || (sender as FrameworkElement)?.Tag is not NetworkMembershipRow row)
        {
            return;
        }
        _networkBusy = true;
        try
        {
            await Account.MoveNetworkAsync(row, delta);
        }
        finally
        {
            _networkBusy = false;
        }
    }

    private async void NetworkLeave_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NetworkMembershipRow row)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Leave Network",
            Content = $"Leave {row.Label}? You'll need a join code to rejoin, and its servers will no longer be seeded by you.",
            PrimaryButtonText = "Leave",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _networkBusy)
        {
            return;
        }
        _networkBusy = true;
        try
        {
            await Account.LeaveNetworkAsync(row);
        }
        finally
        {
            _networkBusy = false;
        }
    }

    /// <summary>Join-a-network dialog: tag + join code. The code is sent and forgotten — never
    /// stored. On failure the dialog stays open with the API's uniform error inline.</summary>
    private async void NetworkJoin_Click(object sender, RoutedEventArgs e)
    {
        var tagBox = new TextBox { PlaceholderText = "Network name", MaxLength = 64 };
        var codeBox = new PasswordBox { PlaceholderText = "Join code", MaxLength = 128 };
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush)
            && brush is Microsoft.UI.Xaml.Media.Brush critical)
        {
            errorText.Foreground = critical;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Enter the network name and join code you received from a participating network.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        content.Children.Add(tagBox);
        content.Children.Add(codeBox);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Join a Network",
            Content = content,
            PrimaryButtonText = "Join",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var error = await Account.JoinNetworkAsync(tagBox.Text, codeBox.Password);
                if (error is not null)
                {
                    args.Cancel = true; // keep the dialog open with the inline error
                    errorText.Text = error;
                    errorText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    // ── Settings toggles ────────────────────────────────────────────────────

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _window.SetTheme(tag switch
            {
                "light" => ElementTheme.Light,
                "system" => ElementTheme.Default,
                _ => ElementTheme.Dark,
            });
        }
    }

    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _config.SetString("close_to_tray", CloseToTrayToggle.IsOn ? "true" : "false");
    }

    private void SwitchNotificationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _config.SetString("switch_notification", SwitchNotificationToggle.IsOn ? "true" : "false");
    }

    private void SplashDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        if (SplashDurationCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _config.SetString("splash_bypass_duration", tag);
        }
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    private void BetaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _config.SetString("update_channel", BetaToggle.IsOn ? "beta" : "stable");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        // Use the informational version so a beta build's pre-release suffix (e.g. "-beta.1") is kept —
        // the numeric AssemblyVersion alone would make the next beta look like a downgrade. See AppVersion.
        var current = AppVersion.ForUpdateCheck(typeof(App).Assembly);

        UpdateSpinner.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for updates…";
        try
        {
            UpdateInfo? info;
            try
            {
                info = await _updater.CheckForUpdatesAsync(current, _config.GetString("update_channel"));
            }
            catch (Exception)
            {
                UpdateStatus.Text = "Update check failed";
                await AlertAsync("Update Check Failed",
                    "Couldn't reach the update server. Check your connection and try again.");
                return;
            }

            if (info is null)
            {
                UpdateStatus.Text = $"You're up to date (v{current})";
                await AlertAsync("Up to Date", $"You're running the latest version (v{current}).");
                return;
            }

            UpdateStatus.Text = $"Update available: v{info.Version}";
            if (!await ConfirmUpdateAsync(info))
            {
                return;
            }

            UpdateStatus.Text = "Downloading update…";
            string installerPath;
            try
            {
                installerPath = await _updater.DownloadAndVerifyAsync(info);
                // info.Sha256 is guaranteed non-null here — DownloadAndVerifyAsync throws without it.
                _updater.LaunchInstaller(installerPath, info.Sha256!);
            }
            catch (Exception)
            {
                UpdateStatus.Text = "Update failed";
                await AlertAsync("Update Failed",
                    "The update couldn't be downloaded or verified. Please try again later.");
                return;
            }

            // Installer is running — shut down so it can replace the running exe.
            _window.QuitForUpdate();
        }
        finally
        {
            UpdateSpinner.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>Show the "update available" prompt with release notes; true if the user accepts.</summary>
    private async Task<bool> ConfirmUpdateAsync(UpdateInfo info)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "A new version is available. Download and install it now? The app will close to apply the update.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (info.Notes.Length > 0)
        {
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 200,
                Content = new TextBlock { Text = info.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 },
            });
        }

        var dialog = new ContentDialog
        {
            Title = $"Update Available: v{info.Version}",
            Content = content,
            PrimaryButtonText = "Update Now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
