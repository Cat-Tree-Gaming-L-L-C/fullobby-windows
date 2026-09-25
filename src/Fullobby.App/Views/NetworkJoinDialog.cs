using Fullobby.App.ViewModels;
using Fullobby.Core.Api;
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
    private const string LinkPrompt = "Paste the invite link a network's staff sent you.";
    private const string CodePrompt =
        "Enter the network name and join code you received. Invite links are preferred; codes are for networks that still hand them out.";

    /// <summary>Show the join prompt. Returns once the user joins or dismisses; a failed join keeps
    /// the dialog open with the server's reason inline, so a mistyped link or code is corrected in
    /// place.
    ///
    /// <para>The primary path is an invite link, taken in two steps on the same dialog: look it up,
    /// then ask "Join &lt;network&gt;?" before joining. A name + join code is the legacy fallback,
    /// behind "Have a join code instead?". <paramref name="inviteLink"/> prefills the link box.</para></summary>
    public static async Task ShowAsync(AccountViewModel account, XamlRoot xamlRoot, string? inviteLink = null)
    {
        var linkBox = new TextBox { PlaceholderText = "Invite link", MaxLength = 512, Text = inviteLink ?? "" };
        var tagBox = new TextBox { PlaceholderText = "Network name", MaxLength = 64 };
        var codeBox = new PasswordBox { PlaceholderText = "Join code", MaxLength = 128 };
        var codePanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        codePanel.Children.Add(tagBox);
        codePanel.Children.Add(codeBox);

        var promptText = new TextBlock
        {
            Text = LinkPrompt,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        };
        // The "Join <network>?" question, shown once a link has been looked up.
        var questionText = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var detailText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
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
        var modeLink = new HyperlinkButton
        {
            Content = "Have a join code instead?",
            FontSize = 12,
            Padding = new Thickness(0),
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(promptText);
        content.Children.Add(linkBox);
        content.Children.Add(codePanel);
        content.Children.Add(questionText);
        content.Children.Add(detailText);
        content.Children.Add(errorText);
        content.Children.Add(modeLink);

        var dialog = new ContentDialog
        {
            Title = "Join a Network",
            Content = content,
            PrimaryButtonText = "Next",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var codeMode = false;
        InvitePreview? previewed = null; // the looked-up link, awaiting "Join"

        // Back to "paste a link, press Next": a changed link voids the question asked about the old one.
        void Reset()
        {
            previewed = null;
            questionText.Visibility = Visibility.Collapsed;
            detailText.Visibility = Visibility.Collapsed;
            errorText.Visibility = Visibility.Collapsed;
            dialog.PrimaryButtonText = codeMode ? "Join" : "Next";
            dialog.CloseButtonText = "Cancel";
        }

        void ShowError(string message)
        {
            errorText.Text = message;
            errorText.Visibility = Visibility.Visible;
        }

        linkBox.TextChanged += (_, _) => Reset();
        modeLink.Click += (_, _) =>
        {
            codeMode = !codeMode;
            linkBox.Visibility = codeMode ? Visibility.Collapsed : Visibility.Visible;
            codePanel.Visibility = codeMode ? Visibility.Visible : Visibility.Collapsed;
            promptText.Text = codeMode ? CodePrompt : LinkPrompt;
            modeLink.Content = codeMode ? "Use an invite link instead" : "Have a join code instead?";
            Reset();
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                errorText.Visibility = Visibility.Collapsed;
                if (codeMode)
                {
                    var codeError = await account.JoinNetworkAsync(tagBox.Text, codeBox.Password);
                    if (codeError is not null)
                    {
                        args.Cancel = true; // keep the dialog open with the inline error
                        ShowError(codeError);
                    }
                    return;
                }

                if (previewed is null)
                {
                    // Step one: look the link up, and ask before joining.
                    args.Cancel = true;
                    var (preview, error) = await account.PreviewInviteAsync(linkBox.Text);
                    if (preview is null)
                    {
                        ShowError(error ?? "Couldn't read that invite link.");
                        return;
                    }
                    previewed = preview;
                    questionText.Text = AccountViewModel.InviteQuestion(preview);
                    questionText.Visibility = Visibility.Visible;
                    var detail = AccountViewModel.InviteDetail(preview);
                    detailText.Text = detail;
                    detailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (preview.Already)
                    {
                        // Already a member: nothing to accept, so leave only the way out.
                        dialog.PrimaryButtonText = "";
                        dialog.CloseButtonText = "Close";
                    }
                    else
                    {
                        dialog.PrimaryButtonText = "Join";
                    }
                    return;
                }

                // Step two: join.
                var acceptError = await account.AcceptInviteAsync(linkBox.Text);
                if (acceptError is not null)
                {
                    args.Cancel = true;
                    ShowError(acceptError);
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
