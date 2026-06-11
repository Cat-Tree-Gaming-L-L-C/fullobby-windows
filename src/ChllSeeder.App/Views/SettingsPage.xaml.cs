using ChllSeeder.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChllSeeder.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ConfigService _config;

    // Suppresses the Toggled handlers while seeding the switches from config.
    private bool _loading;

    public SettingsPage()
    {
        _config = App.AppHost.Services.GetRequiredService<ConfigService>();
        InitializeComponent();

        _loading = true;
        CloseToTrayToggle.IsOn = _config.GetBool("close_to_tray", true);
        SwitchNotificationToggle.IsOn = _config.GetBool("switch_notification", false);
        _loading = false;
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
}
