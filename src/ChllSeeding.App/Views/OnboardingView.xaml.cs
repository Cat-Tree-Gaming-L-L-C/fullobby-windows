using System.ComponentModel;
using ChllSeeding.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChllSeeding.App.Views;

/// <summary>
/// First-run onboarding wizard overlay (sign-in → link → nickname → done), hosted in the
/// shell and shown while <see cref="AccountViewModel.ShowOnboarding"/> is true. Port of
/// <c>src-rust/src/components/onboarding.rs</c>. The step panels are toggled in code-behind
/// from the VM's <c>OnboardingStep</c> so we don't need an int→visibility converter.
/// </summary>
public sealed partial class OnboardingView : UserControl
{
    public AccountViewModel Account { get; }

    // Suppresses the opt-out Toggled handler while we seed the toggle from VM state.
    private bool _loading;

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
        if (e.PropertyName is nameof(AccountViewModel.OnboardingStep) or nameof(AccountViewModel.User))
        {
            UpdateStep();
        }
    }

    private void UpdateStep()
    {
        var step = Account.OnboardingStep;
        StepLabel.Text = $"Step {step + 1} of 4";
        StepSignIn.Visibility = Vis(step == 0);
        StepLink.Visibility = Vis(step == 1);
        StepNickname.Visibility = Vis(step == 2);
        StepDone.Visibility = Vis(step == 3);

        if (step == 1)
        {
            UpdateLinkStep();
        }
        else if (step == 2)
        {
            UpdateNicknameStep();
        }
        else if (step == 3)
        {
            UpdateDoneStep();
        }
    }

    /// <summary>Reflect linked-provider state into the link step (status rows, helper copy,
    /// button labels). Port of <c>onboarding.rs StepLinkAccounts</c>.</summary>
    private void UpdateLinkStep()
    {
        var steam = Account.SteamLinked;
        var discord = Account.DiscordLinked;

        SteamLinkedRow.Visibility = Vis(steam);
        SteamSignedInBadge.Visibility = Vis(steam && Account.IsSteamSignedIn);
        // Steam allows multiple accounts, so the button stays; it just relabels.
        LinkSteamButton.Content = steam ? "Link Another Steam Account" : "Link Steam Account";
        SteamLinkHelp.Visibility = Vis(!steam);

        DiscordLinkedRow.Visibility = Vis(discord);
        DiscordSignedInBadge.Visibility = Vis(discord && Account.IsDiscordSignedIn);
        LinkDiscordButton.Visibility = Vis(!discord);
        DiscordLinkHelp.Visibility = Vis(!discord);

        var anyLinked = steam || discord;
        LinkContinueButton.Content = anyLinked ? "Continue" : "Skip for now";
        LinkLaterHint.Visibility = Vis(!anyLinked);
    }

    /// <summary>Apply guest/OAuth copy and seed the leaderboard opt-out toggle. Port of
    /// <c>onboarding.rs StepNickname</c>.</summary>
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

    /// <summary>Summarise the account + linked providers on the final step. Port of
    /// <c>onboarding.rs StepComplete</c>.</summary>
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
        // User unchanged on error), matching the Rust toggle bound directly to USER.
        _loading = true;
        OnboardLeaderboardToggle.IsOn = Account.ShowOnLeaderboard;
        _loading = false;
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ── Navigation ──────────────────────────────────────────────────────────

    private void ToNickname_Click(object sender, RoutedEventArgs e) => Account.SetOnboardingStep(2);

    private void ToDone_Click(object sender, RoutedEventArgs e) => Account.SetOnboardingStep(3);

    private async void SaveName_Click(object sender, RoutedEventArgs e)
    {
        await Account.UpdateDisplayNameAsync(NicknameBox.Text);
        Account.SetOnboardingStep(3);
    }

    private void Finish_Click(object sender, RoutedEventArgs e) => Account.CompleteOnboarding();

    /// <summary>Escape hatch so a dead backend (guest registration failing) can't trap first-run.
    /// Deliberate deviation from the Rust wizard, which has no step-0 skip.</summary>
    private void Skip_Click(object sender, RoutedEventArgs e) => Account.CompleteOnboarding();
}
