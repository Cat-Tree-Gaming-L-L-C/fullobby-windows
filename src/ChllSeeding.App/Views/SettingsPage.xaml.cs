using ChllSeeding.App.ViewModels;
using ChllSeeding.Core.Config;
using ChllSeeding.Core.Platform;
using ChllSeeding.Core.Scheduling;
using ChllSeeding.Core.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeding.App.Views;

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
    }

    /// <summary>Initialize every control from current config / VM state without firing handlers.</summary>
    private void SeedControls()
    {
        _loading = true;
        EuServersToggle.IsOn = _config.GetBool("eu_enabled");
        DarkModeToggle.IsOn = _window.IsDarkTheme;
        CloseToTrayToggle.IsOn = _config.GetBool("close_to_tray", true);
        SwitchNotificationToggle.IsOn = _config.GetBool("switch_notification", true);
        EfficiencyToggle.IsOn = _config.GetBool("efficiency_mode");
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
        BetaUpdatesToggle.IsOn = _config.GetString("update_channel") == "beta";
        VersionText.Text = $"Current version: v{UpdaterService.CurrentVersion}";
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
            UpdateAutoseedRow(s.NaInstalled, s.NaUtcTime, s.NaNextRun, AutoseedNaStatus, AutoseedNaSetup, AutoseedNaView, AutoseedNaRemove);
            UpdateAutoseedRow(s.EuInstalled, s.EuUtcTime, s.EuNextRun, AutoseedEuStatus, AutoseedEuSetup, AutoseedEuView, AutoseedEuRemove);
        }
        catch (Exception)
        {
            AutoseedNaStatus.Text = "Status unavailable";
            AutoseedEuStatus.Text = "Status unavailable";
        }
    }

    private static void UpdateAutoseedRow(
        bool installed, string? utc, string? nextRun, TextBlock status, Button setup, Button view, Button remove)
    {
        if (installed)
        {
            var text = utc is not null ? $"Daily at {utc} UTC" : "Scheduled";
            if (!string.IsNullOrEmpty(nextRun))
            {
                text += $" · next: {nextRun}";
            }
            status.Text = text;
            setup.Content = "Change";
            view.Visibility = Visibility.Visible;
            remove.Visibility = Visibility.Visible;
        }
        else
        {
            status.Text = "Not scheduled";
            setup.Content = "Set up";
            view.Visibility = Visibility.Collapsed;
            remove.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoseedNaSetup_Click(object sender, RoutedEventArgs e) => _ = SetupAutoseedAsync("na", "12:00");

    private void AutoseedEuSetup_Click(object sender, RoutedEventArgs e) => _ = SetupAutoseedAsync("eu", "06:00");

    private void AutoseedNaView_Click(object sender, RoutedEventArgs e) => _ = ViewAutoseedAsync("na");

    private void AutoseedEuView_Click(object sender, RoutedEventArgs e) => _ = ViewAutoseedAsync("eu");

    private void AutoseedNaRemove_Click(object sender, RoutedEventArgs e) => _ = RemoveAutoseedAsync("na");

    private void AutoseedEuRemove_Click(object sender, RoutedEventArgs e) => _ = RemoveAutoseedAsync("eu");

    private async Task SetupAutoseedAsync(string region, string defaultUtc)
    {
        var label = region.ToUpperInvariant();
        var localHint = AutoSeedTime.TryParseUtc(defaultUtc, out var h, out var m)
            ? $"\n\n{defaultUtc} UTC = {AutoSeedTime.UtcToLocalDisplay(h, m)} your time."
            : "";

        var input = new TextBox { PlaceholderText = "HH:MM", Text = defaultUtc, MaxLength = 5 };
        var dialog = new ContentDialog
        {
            Title = $"{label} Auto-Seed",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Enter the daily {label} seed time in UTC (24-hour HH:MM).{localHint}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    input,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var message = await _autoseed.SetupAsync(region, input.Text);
            await LoadAutoseedStatusAsync();
            await AlertAsync("Auto-Seed Setup", message);
        }
        catch (FormatException)
        {
            await AlertAsync("Invalid Time", $"Please enter the time as HH:MM (e.g. {defaultUtc}).");
        }
        catch (Exception)
        {
            await AlertAsync("Error", $"Failed to set up the {label} auto-seed task. Check the logs for details.");
        }
    }

    /// <summary>Show the verbose schtasks listing for a region's task. Port of the View button in
    /// render_autoseed_section (view_autoseed_schedule → show_alert).</summary>
    private async Task ViewAutoseedAsync(string region)
    {
        string listing;
        try
        {
            listing = await _autoseed.ViewScheduleAsync(region);
        }
        catch (Exception)
        {
            await AlertAsync("Error", "Failed to read the auto-seed schedule. Check the logs for details.");
            return;
        }

        // The listing is monospace-ish schtasks output; show it scrollable so long output stays usable.
        var dialog = new ContentDialog
        {
            Title = $"{region.ToUpperInvariant()} Auto-Seed Schedule",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = listing,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async Task RemoveAutoseedAsync(string region)
    {
        var label = region == "eu" ? "EU servers" : "auto-seed";
        var confirm = new ContentDialog
        {
            Title = "Uninstall",
            Content = $"Are you sure you want to uninstall the {label} scheduled task?",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var deleted = await _autoseed.UninstallAsync(region);
            await LoadAutoseedStatusAsync();
            await AlertAsync(
                deleted ? "Success" : "Info",
                deleted
                    ? $"Uninstalled the {label} scheduled task."
                    : $"No {(region == "eu" ? "EU " : "")}scheduled task found to uninstall.");
        }
        catch (Exception)
        {
            await LoadAutoseedStatusAsync();
            await AlertAsync("Error", $"Failed to uninstall the {label} task. Check the logs for details.");
        }
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    private void BetaUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _config.SetString("update_channel", BetaUpdatesToggle.IsOn ? "beta" : "stable");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            var update = await _updater.CheckForUpdatesAsync();
            if (update is null)
            {
                await AlertAsync("No Updates", "You are running the latest version.");
                return;
            }

            // Port of the Rust "Update Available" alert, extended (per the Phase 5 plan) to wire the
            // installer download — the Rust UI only showed the URL; download_and_install was unused.
            var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : $"\n\n{update.Notes}";
            var dialog = new ContentDialog
            {
                Title = "Update Available",
                Content = $"A new version (v{update.Version}) is available.{notes}",
                PrimaryButtonText = "Download & Install",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await DownloadAndInstallAsync(update);
        }
        catch (Exception)
        {
            await AlertAsync("Error", "Failed to check for updates.");
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallAsync(UpdateInfo update)
    {
        try
        {
            await _updater.DownloadAndInstallAsync(update);
            // Installer launched — shut down through the normal path so it can replace the exe.
            _window.ForceQuit();
        }
        catch (Exception)
        {
            await AlertAsync("Update Failed", "The update could not be downloaded or verified. Please try again later.");
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
        var dialog = new ContentDialog
        {
            Title = "Change Name",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
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
            await Account.DeleteAccountAsync();
        }
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string provider)
        {
            await Account.UnlinkProviderAsync(provider);
        }
    }

    private async void RemoveSteam_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string steamId)
        {
            await Account.RemoveSteamIdAsync(steamId);
        }
    }

    private async void LeaderboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        await Account.SetShowOnLeaderboardAsync(LeaderboardToggle.IsOn);
    }

    // ── Settings toggles ────────────────────────────────────────────────────

    private async void EuServersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Block disabling EU while an EU auto-seed task still exists — otherwise the CHLL-Seeding-EU
        // scheduled task keeps firing for a region the app no longer seeds. Port of settings.rs:347-378.
        if (!EuServersToggle.IsOn && await _autoseed.IsEuInstalledAsync())
        {
            await AlertAsync("EU Auto-Seed Active",
                "Please remove the EU auto-seed scheduled task before disabling EU seeding.");
            _loading = true;
            EuServersToggle.IsOn = true; // revert
            _loading = false;
            return;
        }

        _config.SetString("eu_enabled", EuServersToggle.IsOn ? "true" : "false");
    }

    private void DarkModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _window.SetTheme(DarkModeToggle.IsOn);
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

    private async void EfficiencyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Warn before enabling — efficiency mode rewrites GameUserSettings.ini.
        if (EfficiencyToggle.IsOn)
        {
            var dialog = new ContentDialog
            {
                Title = "Power Savings",
                Content = "Power Savings temporarily edits your game's GameUserSettings.ini to apply " +
                          "low-resource settings while seeding. Your original settings are backed up and " +
                          "restored when seeding ends.\n\nEnable Power Savings?",
                PrimaryButtonText = "Enable",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _loading = true;
                EfficiencyToggle.IsOn = false;
                _loading = false;
                return;
            }
        }

        _config.SetString("efficiency_mode", EfficiencyToggle.IsOn ? "true" : "false");
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
}
