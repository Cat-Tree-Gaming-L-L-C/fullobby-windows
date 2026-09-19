using System.Diagnostics;
using Fullobby.Core;
using Fullobby.Core.Api;
using Fullobby.Core.Security;
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
/// no client change, and the gate (<c>/me.can_manage_servers</c>) is
/// grant-derived, so future sign-in methods (email/password, tokens) inherit it
/// untouched.
///
/// Because the panel is embedded rather than reimplemented, it also decides what
/// each tier sees once loaded: an org Admin gets the Servers tab for their
/// own org's servers, a network Admin gets Networks. The client only
/// decides whether the door exists.
/// </summary>
public sealed partial class AdminPage : Page
{
    private readonly SeedingApiClient _api;
    private readonly ILogger<AdminPage> _log;

    /// <summary>The panel origin (scheme + host + port) the WebView is allowed to navigate
    /// within — learned from the panel-code URL. Anything else opens in the system browser.
    /// Host alone is not enough: matching on host would treat an http:// downgrade of the same
    /// host as same-origin and carry the panel session over cleartext.</summary>
    private string? _panelOrigin;

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
            var profileDir = Branding.WebViewProfileDir;
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", profileDir);

            // This folder holds the admin panel's session cookies, so it gets the same ACL
            // treatment as the config store. It sits under LOCALAPPDATA (where WebView2 wants it)
            // rather than the config directory, so it isn't covered by that directory's hardening.
            try
            {
                Directory.CreateDirectory(profileDir);
                DirectoryHardening.RestrictToCurrentUser(profileDir, _log);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not prepare the WebView2 profile directory");
            }

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

        // Require an absolute https URL: the single-use panel code rides in this URL's query, so
        // an http:// panel address would put it — and the session it redeems for — on the wire.
        if (!Uri.TryCreate(code.Url, UriKind.Absolute, out var panelUri)
            || !string.Equals(panelUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            _log.LogError("Panel-code URL is not an absolute https URL");
            ShowStatus("The admin panel address could not be resolved. Please try again.", retry: true);
            return;
        }
        _panelOrigin = panelUri.GetLeftPart(UriPartial.Authority);

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
                && !string.Equals(
                    target.GetLeftPart(UriPartial.Authority),
                    _panelOrigin,
                    StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                OpenExternal(args.Uri);
            }
        };

        // The callback page redeems the single-use code and lands on /admin. The panel URL is not
        // guaranteed to carry a query string, so pick the separator rather than assuming '&'.
        var separator = panelUri.Query.Length > 0 ? "&" : "?";
        PanelView.CoreWebView2.Navigate(code.Url + separator + "next=%2Fadmin");
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

    /// <summary>Open a link the embedded panel asked for in the system browser.
    /// Only http/https is honoured: <c>UseShellExecute</c> resolves whatever it is handed, so an
    /// unfiltered URI from web content would let a compromised or XSS'd panel page run
    /// <c>file://</c>/UNC executables or invoke a local protocol handler (<c>ms-msdt:</c>,
    /// <c>search-ms:</c>) as the signed-in admin. The URL is deliberately not logged — it is
    /// attacker-controlled in exactly the case worth logging.</summary>
    private void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || !(string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                 || string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            _log.LogWarning("Blocked a non-web link requested by the admin panel");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to open external link from the admin panel");
        }
    }
}
