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

        if (step == 2)
        {
            // Guests can't set a custom name (random anonymous only); hide the text box + Save.
            var guest = Account.IsGuest;
            NicknameBox.Visibility = Vis(!guest);
            SaveNameButton.Visibility = Vis(!guest);
            if (!guest && NicknameBox.Text.Length == 0)
            {
                NicknameBox.Text = Account.DisplayName;
            }
        }
        else if (step == 3)
        {
            DoneSummary.Text = Account.IsGuest
                ? "Mode: Guest"
                : $"Account: {Account.DisplayName}";
        }
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
