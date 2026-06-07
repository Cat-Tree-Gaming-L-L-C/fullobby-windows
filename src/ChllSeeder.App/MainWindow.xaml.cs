using ChllSeeder.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChllSeeder.App;

public sealed partial class MainWindow : Window
{
    // Same footprint as the Rust app (500x520 content + titlebar/nav chrome)
    private const double LogicalWidth = 500;
    private const double LogicalHeight = 600;

    public MainWindow()
    {
        InitializeComponent();

        Title = Branding.ProductName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // Frameless window: extend content into the titlebar, custom drag region
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // Size in logical units once the XAML root (and its DPI scale) exists
        RootGrid.Loaded += (_, _) =>
        {
            var scale = RootGrid.XamlRoot.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)(LogicalWidth * scale),
                (int)(LogicalHeight * scale)));
        };

        NavView.SelectedItem = SeedNavItem;
    }

    /// <summary>Restore and foreground the window (deep-link / repeat-launch activation).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        Activate();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(Views.SettingsPage));
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var pageType = tag switch
        {
            "seed" => typeof(Views.SeedPage),
            "launch" => typeof(Views.LaunchPage),
            "leaderboard" => typeof(Views.LeaderboardPage),
            "tools" => typeof(Views.ToolsPage),
            _ => null,
        };
        if (pageType is not null)
        {
            NavigateTo(pageType);
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
