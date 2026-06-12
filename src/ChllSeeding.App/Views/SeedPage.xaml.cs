using ChllSeeding.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeding.App.Views;

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
        // Route page-agnostic errors (stop/update failures) to this page's banner while it's shown.
        ViewModel.SetActivePage(isLaunchPage: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Dismiss this page's error banner on leave so it doesn't linger on the next visit.
        ViewModel.ClearSeedError();
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
