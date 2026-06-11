using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace ChllSeeder.App.Services;

/// <summary>Owns the in-app toast stack shown in the shell. Toasts auto-dismiss after a duration
/// and can be closed manually. The shell binds <see cref="Toasts"/>; subsystems call <see cref="Show"/>.
/// Port of the Rust <c>state::toast::add_toast</c> queue.</summary>
public sealed class InAppToastService
{
    private const int DefaultDurationMs = 6000;

    // Captured on the UI thread: the service is first resolved when the shell is built.
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>Live collection bound by the shell's toast host.</summary>
    public ObservableCollection<InAppToast> Toasts { get; } = new();

    public void Info(string message, int durationMs = DefaultDurationMs)
        => Show(message, InfoBarSeverity.Informational, durationMs);

    public void Success(string message, int durationMs = DefaultDurationMs)
        => Show(message, InfoBarSeverity.Success, durationMs);

    public void Warning(string message, int durationMs = DefaultDurationMs)
        => Show(message, InfoBarSeverity.Warning, durationMs);

    public void Error(string message, int durationMs = 12000)
        => Show(message, InfoBarSeverity.Error, durationMs);

    /// <summary>Queue a toast (marshalled to the UI thread). Auto-removes after <paramref name="durationMs"/>;
    /// pass <c>0</c> to keep it until dismissed manually.</summary>
    public void Show(string message, InfoBarSeverity severity, int durationMs = DefaultDurationMs)
    {
        if (_dispatcher.HasThreadAccess)
        {
            Add(message, severity, durationMs);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Add(message, severity, durationMs));
        }
    }

    /// <summary>Remove a toast (manual close button).</summary>
    public void Dismiss(InAppToast toast) => Toasts.Remove(toast);

    private void Add(string message, InfoBarSeverity severity, int durationMs)
    {
        var toast = new InAppToast(message, severity);
        Toasts.Add(toast);

        if (durationMs <= 0)
        {
            return;
        }

        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(durationMs);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }
}
