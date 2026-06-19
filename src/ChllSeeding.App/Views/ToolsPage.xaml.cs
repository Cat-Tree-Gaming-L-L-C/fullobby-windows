using System.Diagnostics;
using ChllSeeding.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ChllSeeding.App.Views;

/// <summary>
/// Tools tab: manual game-config backup/restore, restore from the automatic pre-seeding backup,
/// open the app logs, and web resource links. Port of <c>src-rust/src/components/tools.rs</c>
/// (handler-driven, not MVVM, matching the original).
/// </summary>
public sealed partial class ToolsPage : Page
{
    private readonly ManualBackupService _backup;
    private readonly MainWindow _window;
    private readonly ILogger<ToolsPage> _log;

    public ToolsPage()
    {
        _backup = App.AppHost.Services.GetRequiredService<ManualBackupService>();
        _window = App.AppHost.Services.GetRequiredService<MainWindow>();
        _log = App.AppHost.Services.GetRequiredService<ILogger<ToolsPage>>();
        InitializeComponent();
    }

    // ── Backup / restore ────────────────────────────────────────────────────

    private async void BackupSettings_Click(object sender, RoutedEventArgs e)
    {
        var source = await PickFolderAsync();
        if (source is null)
        {
            return;
        }

        try
        {
            var count = await _backup.BackupUserSettingsAsync(source);
            await AlertAsync("Backup Complete",
                $"Backed up {count} config file(s) to {_backup.GetManualBackupPath()}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual backup failed");
            await AlertAsync("Error", "Failed to back up game settings. Check the logs for details.");
        }
    }

    private async void RestoreManual_Click(object sender, RoutedEventArgs e)
    {
        if (!_backup.HasBackup())
        {
            await AlertAsync("Info", "No manual backup found. Please create a backup first.");
            return;
        }

        var backupFolder = await PickFolderAsync();
        if (backupFolder is null)
        {
            return;
        }
        var dest = await PickFolderAsync();
        if (dest is null)
        {
            return;
        }

        try
        {
            var count = await _backup.RestoreUserSettingsAsync(backupFolder, dest);
            await AlertAsync(count > 0 ? "Restore Complete" : "Info",
                count > 0 ? $"Restored {count} config file(s) from backup." : "No files were restored.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual restore failed");
            await AlertAsync("Error", "Failed to restore game settings. Check the logs for details.");
        }
    }

    private async void RestoreAuto_Click(object sender, RoutedEventArgs e)
    {
        if (!_backup.HasAutoBackup())
        {
            await AlertAsync("Info",
                "No automatic backup found.\n\nAn automatic backup is created before each seeding session.");
            return;
        }

        if (!await ConfirmAsync("Confirm Restore",
                "Restore game settings from the last automatic backup?\n\n" +
                "This was saved before your last seeding session started."))
        {
            return;
        }

        try
        {
            var message = _backup.RestoreFromAutoBackup();
            await AlertAsync("Restore Complete", message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auto-restore failed");
            await AlertAsync("Error", "Failed to restore from automatic backup. Check the logs for details.");
        }
    }

    private async void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _backup.OpenLogs();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open logs");
            await AlertAsync("Error", "Failed to open the logs folder.");
        }
    }

    // ── Links submenu ───────────────────────────────────────────────────────

    private void ShowLinks_Click(object sender, RoutedEventArgs e)
    {
        MainMenu.Visibility = Visibility.Collapsed;
        LinksMenu.Visibility = Visibility.Visible;
    }

    private void BackToTools_Click(object sender, RoutedEventArgs e)
    {
        LinksMenu.Visibility = Visibility.Collapsed;
        MainMenu.Visibility = Visibility.Visible;
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        // Unpackaged WinUI 3: the picker must be associated with the app window's HWND.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private Task AlertAsync(string title, string content) =>
        new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        }.ShowAsync().AsTask();

    private async Task<bool> ConfirmAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
