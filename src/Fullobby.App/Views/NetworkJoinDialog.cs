using Fullobby.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fullobby.App.Views;

/// <summary>
/// The "join a network" prompt, as a dialog over the running app.
///
/// Shared by the Settings networks section and the shell's join-a-network banner. It used to live
/// only in Settings, while the same task was ALSO expressed as a full-bleed opaque overlay that
/// replaced the entire app whenever an onboarded user was found holding no memberships — which read
/// as being thrown back into onboarding, and was armed by any transient membership loss. One
/// dialog, reachable from wherever the user happens to be, is the whole of that interaction now.
/// </summary>
public static class NetworkJoinDialog
{
    /// <summary>Show the join prompt. Returns once the user joins or dismisses; a failed join keeps
    /// the dialog open with the server's reason inline, so a mistyped code is corrected in place.</summary>
    public static async Task ShowAsync(AccountViewModel account, XamlRoot xamlRoot)
    {
        var tagBox = new TextBox { PlaceholderText = "Network name", MaxLength = 64 };
        var codeBox = new PasswordBox { PlaceholderText = "Join code", MaxLength = 128 };
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush)
            && brush is Microsoft.UI.Xaml.Media.Brush critical)
        {
            errorText.Foreground = critical;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Enter the network name and join code you received from a participating network.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        content.Children.Add(tagBox);
        content.Children.Add(codeBox);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Join a Network",
            Content = content,
            PrimaryButtonText = "Join",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var error = await account.JoinNetworkAsync(tagBox.Text, codeBox.Password);
                if (error is not null)
                {
                    args.Cancel = true; // keep the dialog open with the inline error
                    errorText.Text = error;
                    errorText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }
}
