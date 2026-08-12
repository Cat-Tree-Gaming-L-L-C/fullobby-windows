using System.Diagnostics;
using Fullobby.Core.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fullobby.App.Views;

/// <summary>
/// Admin tab: hosts the existing web admin panel in a WebView2, signed in
/// automatically from the app's session via a one-time exchange code
/// (<c>POST /api/auth/panel-code</c>). The web panel stays the single admin
/// surface (alongside the DM console) — every panel feature appears here with
/// no client change, and the gate (<c>/me.is_admin</c>) is grant-derived, so
/// future sign-in methods (email/password, tokens) inherit it untouched.
/// </summary>
public sealed partial class AdminPage : Page
{
    private readonly SeedingApiClient _api;
    private readonly ILogger<AdminPage> _log;

    /// <summary>The panel origin the WebView is allowed to navigate within —
    /// learned from the panel-code URL. Anything else opens in the system browser.</summary>
    private string? _panelHost;

    private bool _signedIn;

    public AdminPage()
    {
        _api = App.AppHost.Services.GetRequiredService<SeedingApiClient>();
        _log = App.AppHost.Services.GetRequiredService<ILogger<AdminPage>>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_signedIn)
        {
            await SignInPanelAsync();
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await SignInPanelAsync();

    private async Task SignInPanelAsync()
    {
        ShowStatus("Signing in to the admin panel…", retry: false);
        try
        {
            // WebView2 needs a writable user-data folder; the default for an
            // unpackaged app is next to the exe (Program Files — read-only).
            Environment.SetEnvironmentVariable(
                "WEBVIEW2_USER_DATA_FOLDER",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "com.fullobby.app", "webview2"));
            await PanelView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WebView2 runtime unavailable");
            ShowStatus(
                "The admin panel needs the Microsoft WebView2 runtime, which isn't installed. " +
                "Install it from Microsoft and try again — or use the web panel in your browser.",
                retry: true);
            return;
        }

        PanelCodeResponse code;
        try
        {
            code = await _api.GetPanelCodeAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to mint a panel sign-in code");
            ShowStatus(ApiValidation.FriendlyError(ex.Message), retry: true);
            return;
        }

        _panelHost = Uri.TryCreate(code.Url, UriKind.Absolute, out var u) ? u.Host : null;
        if (_panelHost is null)
        {
            _log.LogError("Panel-code URL unparseable");
            ShowStatus("The admin panel address could not be resolved. Please try again.", retry: true);
            return;
        }

        // Keep the embedded session inside the panel: same-origin navigation only.
        // External links (Discord invites, docs) go to the system browser, so the
        // panel's JWT never rides along to a third-party page.
        PanelView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };
        PanelView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)
                && !string.Equals(target.Host, _panelHost, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
        };

        // The callback page redeems the single-use code and lands on /admin.
        PanelView.CoreWebView2.Navigate(code.Url + "&next=%2Fadmin");
        _signedIn = true;
        StatusPanel.Visibility = Visibility.Collapsed;
        PanelView.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string message, bool retry)
    {
        PanelView.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        Spinner.IsActive = !retry;
        StatusText.Text = message;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to open external link from the admin panel");
        }
    }
}
