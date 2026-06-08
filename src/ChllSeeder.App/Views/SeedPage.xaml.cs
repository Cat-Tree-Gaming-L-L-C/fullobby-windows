using ChllSeeder.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeder.App.Views;

public sealed partial class SeedPage : Page
{
    public SeedingViewModel ViewModel { get; }

    public SeedPage()
    {
        // Pages are created by Frame.Navigate (parameterless ctor), so resolve the shared
        // (singleton) view model from DI here.
        ViewModel = App.AppHost.Services.GetRequiredService<SeedingViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Provide the "close the running game?" confirmation using this page's XamlRoot.
        ViewModel.ConfirmAsync = ConfirmAsync;
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
