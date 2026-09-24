using Fullobby.App.ViewModels;
using Fullobby.Core.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fullobby.App.Views;

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
        ViewModel.ChooseSwapAsync = ChooseSwapAsync;
        // Route page-agnostic errors (stop/update failures) to this page's banner while it's shown.
        ViewModel.SetActivePage(isLaunchPage: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Dismiss this page's error banner on leave so it doesn't linger on the next visit.
        ViewModel.ClearLaunchError();
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

    /// <summary>The close-before-seeding question: "Close …" / "Continue anyway" (only for another
    /// Unreal game, where the check can be wrong) / "Cancel". Cancel is the default — nothing closes
    /// on a stray Enter.</summary>
    private async Task<GameSwapChoice> ChooseSwapAsync(GameSwapPlan plan)
    {
        var dialog = new ContentDialog
        {
            Title = plan.Title,
            Content = new TextBlock { Text = plan.ConfirmMessage, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = plan.Kind == GameSwapKind.Relaunch ? "Close game" : $"Close {plan.ClosingNames}",
            SecondaryButtonText = plan.AllowContinueAnyway ? "Continue anyway" : "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => GameSwapChoice.Close,
            ContentDialogResult.Secondary => GameSwapChoice.ContinueAnyway,
            _ => GameSwapChoice.Cancel,
        };
    }
}
