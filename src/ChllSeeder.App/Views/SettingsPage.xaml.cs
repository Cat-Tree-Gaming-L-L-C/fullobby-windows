using ChllSeeder.App.ViewModels;
using ChllSeeder.Core.Config;
using ChllSeeder.Core.Platform;
using ChllSeeder.Core.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeder.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ConfigService _config;
    private readonly MainWindow _window;
    private readonly StartupRegistry _startup;
    private readonly AutoSeedService _autoseed;

    public AccountViewModel Account { get; }

    // Suppresses the Toggled/SelectionChanged handlers while seeding controls from config.
    private bool _loading;

    public SettingsPage()
    {
        _config = App.AppHost.Services.GetRequiredService<ConfigService>();
        _window = App.AppHost.Services.GetRequiredService<MainWindow>();
        _startup = App.AppHost.Services.GetRequiredService<StartupRegistry>();
        _autoseed = App.AppHost.Services.GetRequiredService<AutoSeedService>();
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
            UpdateAutoseedRow(s.NaInstalled, s.NaUtcTime, s.NaNextRun, AutoseedNaStatus, AutoseedNaSetup, AutoseedNaRemove);
            UpdateAutoseedRow(s.EuInstalled, s.EuUtcTime, s.EuNextRun, AutoseedEuStatus, AutoseedEuSetup, AutoseedEuRemove);
        }
        catch (Exception)
        {
            AutoseedNaStatus.Text = "Status unavailable";
            AutoseedEuStatus.Text = "Status unavailable";
        }
    }

    private static void UpdateAutoseedRow(
        bool installed, string? utc, string? nextRun, TextBlock status, Button setup, Button remove)
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
            remove.Visibility = Visibility.Visible;
        }
        else
        {
            status.Text = "Not scheduled";
            setup.Content = "Set up";
            remove.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoseedNaSetup_Click(object sender, RoutedEventArgs e) => _ = SetupAutoseedAsync("na", "12:00");

    private void AutoseedEuSetup_Click(object sender, RoutedEventArgs e) => _ = SetupAutoseedAsync("eu", "06:00");

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

    private async Task RemoveAutoseedAsync(string region)
    {
        try
        {
            await _autoseed.UninstallAsync(region);
        }
        catch (Exception)
        {
            // Removal failures are non-fatal; reflect whatever the current status is.
        }
        await LoadAutoseedStatusAsync();
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

    private void EuServersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
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
