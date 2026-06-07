using CommunityToolkit.Mvvm.ComponentModel;

namespace ChllSeeder.App.ViewModels;

/// <summary>
/// Minimal Phase 0 view model proving the CommunityToolkit.Mvvm source-gen
/// pipeline. Real seeding state (engine status, server lists) replaces this
/// in Phase 1.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    private string seedStatusText = "Phase 0 skeleton — the seeding engine arrives in Phase 1.";
}
