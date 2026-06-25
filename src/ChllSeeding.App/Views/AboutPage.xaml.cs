using System.Diagnostics;
using ChllSeeding.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChllSeeding.App.Views;

/// <summary>
/// About tab: app identity, a short seeding FAQ, community links, and the brand
/// wordmark. Links are sourced from <see cref="Branding"/> so URLs live in one place.
/// </summary>
public sealed partial class AboutPage : Page
{
    private readonly ILogger<AboutPage> _log;

    // Bound by the link buttons' Tag in XAML — keeps URLs centralized in Branding.
    public string DiscordUrl => Branding.DiscordUrl;
    public string WebsiteUrl => Branding.WebsiteUrl;
    public string GitHubUrl => Branding.GitHubUrl;
    public string FaqUrl => Branding.FaqUrl;
    public string TermsUrl => Branding.TermsUrl;
    public string PrivacyUrl => Branding.PrivacyUrl;

    public AboutPage()
    {
        _log = App.AppHost.Services.GetRequiredService<ILogger<AboutPage>>();
        InitializeComponent();
        VersionLine.Text = $"v{GetType().Assembly.GetName().Version?.ToString(3)} · {Branding.Publisher}";
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to open link {Url}", url);
            }
        }
    }
}
