using System.Diagnostics;
using Fullobby.Core.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fullobby.App.Views;

/// <summary>
/// Manage tab: opens the web panel (<c>/admin</c>) in the system browser, where the user signs in
/// on their own. The panel stays the single admin surface (alongside the DM console), and what
/// each tier sees there is the panel's call: the client only decides whether the door exists
/// (<c>/me</c>, see <c>MainWindow.UpdateManageVisibility</c>).
///
/// <para>This tab used to embed the panel in a WebView2, signed in from the app's session with a
/// single-use code (<c>POST /api/auth/panel-code</c>). That let anything holding the app's stored
/// tokens act as the user in the panel, with their staff powers, without ever signing in to it.
/// The panel now wants a sign-in of its own, so the server can retire the handoff. The path is on
/// the API host, which redirects panel pages to the app host.</para>
/// </summary>
public sealed partial class AdminPage : Page
{
    private readonly ILogger<AdminPage> _log;

    public AdminPage()
    {
        _log = App.AppHost.Services.GetRequiredService<ILogger<AdminPage>>();
        InitializeComponent();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        // The base is hardbaked https in Release; a Debug override is checked anyway, since
        // UseShellExecute launches whatever it is handed.
        if (!Uri.TryCreate(ApiConfig.BaseUrl + "/admin", UriKind.Absolute, out var panel)
            || !(string.Equals(panel.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                 || string.Equals(panel.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            _log.LogError("The panel address is not a web URL");
            ShowError("The panel address could not be resolved.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(panel.AbsoluteUri) { UseShellExecute = true });
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open the panel in the browser");
            ShowError("Your browser didn't open. Go to " + panel.AbsoluteUri + " instead.");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
