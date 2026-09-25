using System.ComponentModel;
using Fullobby.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fullobby.App.Views;

/// <summary>
/// First-run onboarding wizard overlay (sign-in → join network → link → nickname → done),
/// hosted in the shell and shown while <see cref="AccountViewModel.ShowOnboarding"/> is true. The
/// step panels are toggled in code-behind from the VM's <c>OnboardingStep</c> so we don't need an
/// int→visibility converter.
///
/// First run only. The limited-beta network gate used to re-open this overlay for already-onboarded
/// users holding no membership; it is now a banner in the shell
/// (<see cref="AccountViewModel.NetworkGateActive"/>), so nothing re-enters the wizard except a
/// user who has genuinely never finished it.
/// </summary>
public sealed partial class OnboardingView : UserControl
{
    public AccountViewModel Account { get; }

    // Suppresses the opt-out Toggled handler while we seed the toggle from VM state.
    private bool _loading;

    // Last rendered step, so entering the network step (and only entering it) refreshes the
    // membership list — a returning user with memberships gets Continue enabled instantly.
    private int _lastStep = -1;

    public OnboardingView()
    {
        Account = App.AppHost.Services.GetRequiredService<AccountViewModel>();
        InitializeComponent();

        Account.PropertyChanged += OnAccountPropertyChanged;
        Loaded += (_, _) => UpdateStep();
        Unloaded += (_, _) => Account.PropertyChanged -= OnAccountPropertyChanged;
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.OnboardingStep) or nameof(AccountViewModel.User)
            or nameof(AccountViewModel.HasNetworkMembership)
            or nameof(AccountViewModel.HasLinkConflict) or nameof(AccountViewModel.ShowReauthOnly))
        {
            UpdateStep();
        }
        else if (e.PropertyName == nameof(AccountViewModel.PendingInvite) && Account.OnboardingStep == 1)
        {
            PrefillPendingInvite();
        }
    }

    private void UpdateStep()
    {
        var step = Account.OnboardingStep;
        StepLabel.Text = $"Step {step + 1} of 5";
        // A plain re-authentication is not a wizard walk-through — hide the step counter.
        StepLabel.Visibility = Vis(!Account.ShowReauthOnly);
        StepSignIn.Visibility = Vis(step == 0);
        UpdateSignInCopy();
        StepNetwork.Visibility = Vis(step == 1);
        StepLink.Visibility = Vis(step == 2);
        StepNickname.Visibility = Vis(step == 3);
        StepDone.Visibility = Vis(step == 4);

        if (step == 1)
        {
            UpdateNetworkStep();
            PrefillPendingInvite();
            if (_lastStep != 1)
            {
                // Refresh on entry so a user who already belongs to a network passes instantly.
                _ = Account.RefreshNetworksAsync();
            }
        }
        else if (step == 2)
        {
            UpdateLinkStep();
        }
        else if (step == 3)
        {
            UpdateNicknameStep();
        }
        else if (step == 4)
        {
            UpdateDoneStep();
        }
        _lastStep = step;
    }

    /// <summary>Word the sign-in step for why it is being shown. A configured install whose
    /// credentials went missing is not being onboarded — it is being asked to sign in, and telling
    /// that user "Welcome to Fullobby" is what made this read as starting over.</summary>
    private void UpdateSignInCopy()
    {
        var reauth = Account.ShowReauthOnly;
        SignInHeading.Text = reauth ? "Welcome back" : "Welcome to Fullobby";
        SignInSubtext.Text = reauth
            ? "Your session expired. Sign in again to pick up where you left off — your setup is still here."
            : "Sign in to track your seeding contributions, or continue as a guest.";
    }

    /// <summary>Reflect membership state into the network step: Continue unlocks at ≥1 membership.
    /// First-run only now — the post-onboarding gate is a shell banner, not a wizard step.</summary>
    private void UpdateNetworkStep()
    {
        NetworkContinueButton.IsEnabled = Account.HasNetworkMembership;
        NetworkListHeader.Visibility = Vis(Account.Networks.Count > 0);
    }

    /// <summary>Reflect linked-provider state into the link step (status rows, helper copy,
    /// button labels).</summary>
    private void UpdateLinkStep()
    {
        var steam = Account.SteamLinked;
        var discord = Account.DiscordLinked;

        SteamLinkedRow.Visibility = Vis(steam);
        SteamSignedInBadge.Visibility = Vis(steam && Account.IsSteamSignedIn);
        // One Steam account per user (like Discord): hide the button once linked.
        LinkSteamButton.Content = "Link Steam Account";
        LinkSteamButton.Visibility = Vis(!steam);
        SteamLinkHelp.Visibility = Vis(!steam);

        DiscordLinkedRow.Visibility = Vis(discord);
        DiscordSignedInBadge.Visibility = Vis(discord && Account.IsDiscordSignedIn);
        LinkDiscordButton.Visibility = Vis(!discord);
        DiscordLinkHelp.Visibility = Vis(!discord);

        var anyLinked = steam || discord;
        LinkContinueButton.Content = anyLinked ? "Continue" : "Skip for now";
        LinkLaterHint.Visibility = Vis(!anyLinked);

        UpdateLinkConflictPanel();
    }

    /// <summary>Show the recovery panel for a refused link, labelled with the provider the user
    /// has to sign in with instead.</summary>
    private void UpdateLinkConflictPanel()
    {
        var conflict = Account.HasLinkConflict;
        LinkConflictPanel.Visibility = Vis(conflict);
        if (!conflict)
        {
            return;
        }
        LinkConflictText.Text = Account.LinkConflictMessage;
        var provider = Account.LinkConflictProvider ?? "discord";
        LinkConflictSignInButton.Content =
            $"Sign in with {char.ToUpperInvariant(provider[0])}{provider[1..]} instead";
    }

    /// <summary>Apply guest/OAuth copy and seed the leaderboard opt-out toggle.</summary>
    private void UpdateNicknameStep()
    {
        // Guests can't set a custom name (random anonymous only); hide the text box + Save.
        var guest = Account.IsGuest;
        NicknameBox.Visibility = Vis(!guest);
        SaveNameButton.Visibility = Vis(!guest);
        if (!guest && NicknameBox.Text.Length == 0)
        {
            NicknameBox.Text = Account.DisplayName;
        }

        NicknameHeading.Text = guest ? "Randomize a Nickname" : "Choose a Nickname";
        NicknameSubtext.Text = guest
            ? "Guest accounts use a random anonymous name on the leaderboard."
            : "Pick a display name for the leaderboard, or go anonymous.";
        RandomizeNameButton.Content = guest ? "Randomize Name" : "Go Anonymous";

        _loading = true;
        OnboardLeaderboardToggle.IsOn = Account.ShowOnLeaderboard;
        _loading = false;
    }

    /// <summary>Summarise the account + linked providers on the final step.</summary>
    private void UpdateDoneStep()
    {
        var guest = Account.IsGuest;
        DoneSummary.Text = guest ? "Mode: Guest" : $"Account: {Account.DisplayName}";
        DoneSteamLine.Visibility = Vis(!guest && Account.SteamLinked);
        DoneDiscordLine.Visibility = Vis(!guest && Account.DiscordLinked);
    }

    private async void OnboardLeaderboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        await Account.SetShowOnLeaderboardAsync(OnboardLeaderboardToggle.IsOn);
        // Re-seed from VM state so a failed update reverts the toggle (the API call leaves
        // User unchanged on error).
        _loading = true;
        OnboardLeaderboardToggle.IsOn = Account.ShowOnLeaderboard;
        _loading = false;
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ── Network step ────────────────────────────────────────────────────────

    /// <summary>Look up the pasted invite link and ask "Join &lt;network&gt;?" before joining. An org
    /// or staff link, or a dead one, is explained inline instead.</summary>
    private async void InviteCheck_Click(object sender, RoutedEventArgs e)
    {
        InviteCheckButton.IsEnabled = false;
        InviteError.Visibility = Visibility.Collapsed;
        InviteConfirmPanel.Visibility = Visibility.Collapsed;
        try
        {
            var (preview, error) = await Account.PreviewInviteAsync(InviteLinkBox.Text);
            if (preview is null)
            {
                InviteError.Text = error;
                InviteError.Visibility = Visibility.Visible;
                return;
            }
            InviteConfirmTitle.Text = AccountViewModel.InviteQuestion(preview);
            var detail = AccountViewModel.InviteDetail(preview);
            InviteConfirmDetail.Text = detail;
            InviteConfirmDetail.Visibility = Vis(detail.Length > 0);
            // Already a member: nothing to accept, just acknowledge (Continue is enabled by the
            // membership refresh the preview did).
            InviteAcceptButton.Visibility = Vis(!preview.Already);
            InviteDismissButton.Content = preview.Already ? "OK" : "Cancel";
            InviteConfirmPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            InviteCheckButton.IsEnabled = true;
        }
    }

    /// <summary>Join through the previewed link. The link is cleared from the box on success and
    /// never written to config.</summary>
    private async void InviteAccept_Click(object sender, RoutedEventArgs e)
    {
        InviteAcceptButton.IsEnabled = false;
        InviteError.Visibility = Visibility.Collapsed;
        try
        {
            var error = await Account.AcceptInviteAsync(InviteLinkBox.Text);
            if (error is null)
            {
                ResetInvite();
            }
            else
            {
                InviteConfirmPanel.Visibility = Visibility.Collapsed;
                InviteError.Text = error;
                InviteError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            InviteAcceptButton.IsEnabled = true;
        }
    }

    private void InviteDismiss_Click(object sender, RoutedEventArgs e) => ResetInvite();

    /// <summary>Put a <c>fullobby://invite</c> link's token in the box. Held until this step: the
    /// lookup needs the account the earlier step signs in.</summary>
    private void PrefillPendingInvite()
    {
        if (Account.TakePendingInvite() is { } token)
        {
            InviteLinkBox.Text = token;
        }
    }

    /// <summary>A changed link invalidates the question asked about the old one.</summary>
    private void InviteLinkBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        InviteConfirmPanel.Visibility = Visibility.Collapsed;
        InviteError.Visibility = Visibility.Collapsed;
    }

    private void ResetInvite()
    {
        InviteLinkBox.Text = ""; // also collapses the panel and error (TextChanged)
        InviteConfirmPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Join with the entered tag + code — the legacy way in, under "Have a join code
    /// instead?". The code is sent and forgotten — cleared from the box on success and never
    /// written to config.</summary>
    private async void JoinNetwork_Click(object sender, RoutedEventArgs e)
    {
        NetworkJoinButton.IsEnabled = false;
        NetworkJoinError.Visibility = Visibility.Collapsed;
        try
        {
            var error = await Account.JoinNetworkAsync(NetworkTagBox.Text, NetworkCodeBox.Password);
            if (error is null)
            {
                NetworkTagBox.Text = "";
                NetworkCodeBox.Password = "";
            }
            else
            {
                NetworkJoinError.Text = error;
                NetworkJoinError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            NetworkJoinButton.IsEnabled = true;
        }
    }

    private void NetworkContinue_Click(object sender, RoutedEventArgs e) => Account.ContinueFromNetworkStep();

    // ── Escape hatches ──────────────────────────────────────────────────────

    /// <summary>Discard the account this run created and return to step 0. The overlay covers the
    /// whole shell (Settings and its Sign Out included), so without this the only way out of a
    /// wrong identity choice was reinstalling.</summary>
    private void StartOver_Click(object sender, RoutedEventArgs e) => Account.StartOverCommand.Execute(null);

    /// <summary>Act on a refused link: leave this account and sign in as the one that owns the
    /// identity.</summary>
    private void LinkConflictSignIn_Click(object sender, RoutedEventArgs e) =>
        Account.SwitchAccountCommand.Execute(Account.LinkConflictProvider ?? "discord");

    private void DismissLinkConflict_Click(object sender, RoutedEventArgs e) => Account.ClearLinkConflict();

    // ── Navigation ──────────────────────────────────────────────────────────

    private void ToNickname_Click(object sender, RoutedEventArgs e) => Account.SetOnboardingStep(3);

    private void ToDone_Click(object sender, RoutedEventArgs e) => Account.SetOnboardingStep(4);

    /// <summary>Randomize always overwrites the box, even if the user already typed a name —
    /// UpdateNicknameStep only fills it when empty, so a stale typed name would otherwise stick
    /// around after a successful randomize.</summary>
    private async void RandomizeName_Click(object sender, RoutedEventArgs e)
    {
        await Account.RandomizeNameCommand.ExecuteAsync(null);
        if (!Account.IsGuest)
        {
            NicknameBox.Text = Account.DisplayName;
        }
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e)
    {
        await Account.UpdateDisplayNameAsync(NicknameBox.Text);
        Account.SetOnboardingStep(4);
    }

    private void Finish_Click(object sender, RoutedEventArgs e) => Account.CompleteOnboarding();
}
