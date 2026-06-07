using ChllSeeder.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace ChllSeeder.App.Views;

public sealed partial class SeedPage : Page
{
    public ShellViewModel ViewModel { get; }

    public SeedPage()
    {
        // Pages are created by Frame.Navigate (parameterless ctor), so resolve from DI here
        ViewModel = App.AppHost.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }
}
