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
/// int→visibility converter. The network step doubles as the limited-beta re-arm portal for
/// already-onboarded users with zero memberships (<see cref="AccountViewModel.NetworkGateActive"/>).
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
            or nameof(AccountViewModel.HasNetworkMembership) or nameof(AccountViewModel.NetworkGateActive))
        {
            UpdateStep();
        }
    }

    private void UpdateStep()
    {
        var step = Account.OnboardingStep;
        StepLabel.Text = $"Step {step + 1} of 5";
        // The re-armed network gate isn't a wizard walk-through — hide the step counter.
        StepLabel.Visibility = Vis(!Account.NetworkGateActive);
        StepSignIn.Visibility = Vis(step == 0);
        StepNetwork.Visibility = Vis(step == 1);
        StepLink.Visibility = Vis(step == 2);
        StepNickname.Visibility = Vis(step == 3);
        StepDone.Visibility = Vis(step == 4);

        if (step == 1)
        {
            UpdateNetworkStep();
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

    /// <summary>Reflect membership state into the network step: Continue unlocks at ≥1 membership;
    /// a re-armed gate (already onboarded) labels the button Done since no wizard follows.</summary>
    private void UpdateNetworkStep()
    {
        NetworkContinueButton.IsEnabled = Account.HasNetworkMembership;
        NetworkContinueButton.Content = Account.NetworkGateActive ? "Done" : "Continue";
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

    /// <summary>Join with the entered tag + code. The code is sent and forgotten — cleared from
    /// the box on success and never written to config.</summary>
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
