using ChllSeeder.App.ViewModels;
using ChllSeeder.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeder.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ConfigService _config;
    private readonly MainWindow _window;

    public AccountViewModel Account { get; }

    // Suppresses the Toggled/SelectionChanged handlers while seeding controls from config.
    private bool _loading;

    public SettingsPage()
    {
        _config = App.AppHost.Services.GetRequiredService<ConfigService>();
        _window = App.AppHost.Services.GetRequiredService<MainWindow>();
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
        _loading = false;
    }

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
