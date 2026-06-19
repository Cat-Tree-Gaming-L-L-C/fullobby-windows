using ChllSeeding.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ChllSeeding.App.Views;

public sealed partial class LeaderboardPage : Page
{
    public LeaderboardViewModel ViewModel { get; }

    public LeaderboardPage()
    {
        ViewModel = App.AppHost.Services.GetRequiredService<LeaderboardViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Refresh on each visit (cheap; the VM's per-fetch cooldown debounces rapid revisits).
        _ = ViewModel.RefreshAsync();
    }
}
