using ChllSeeder.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeder.App.Views;

public sealed partial class LaunchPage : Page
{
    public SeedingViewModel ViewModel { get; }

    public LaunchPage()
    {
        ViewModel = App.AppHost.Services.GetRequiredService<SeedingViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.ConfirmAsync = ConfirmAsync;
    }

    // Per-item launch buttons are bound inside an ItemsControl template (separate namescope),
    // so route the click through code-behind to the shared command.
    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServerRow row)
        {
            ViewModel.LaunchCommand.Execute(row);
        }
    }

    private async Task<bool> ConfirmAsync(string message, string title)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
