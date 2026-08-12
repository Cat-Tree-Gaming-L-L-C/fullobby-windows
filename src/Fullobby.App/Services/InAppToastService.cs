using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Fullobby.App.Services;

/// <summary>Owns the in-app toast stack shown in the shell. Toasts auto-dismiss after a duration
/// and can be closed manually. The shell binds <see cref="Toasts"/>; subsystems call <see cref="Show"/>.</summary>
public sealed class InAppToastService
{
    // Default durations: 3s for info/success/warning, 10s for errors.
    private const int DefaultDurationMs = 3000;
    private const int ErrorDurationMs = 10000;

    /// <summary>Most toasts shown at once; the oldest is dropped past this.</summary>
    private const int MaxToasts = 3;

    // Captured on the UI thread: the service is first resolved when the shell is built.
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    // Per-toast auto-dismiss timers, tracked so a deduped replacement can cancel the old timer.
    private readonly Dictionary<InAppToast, DispatcherQueueTimer> _timers = new();

    /// <summary>Live collection bound by the shell's toast host.</summary>
    public ObservableCollection<InAppToast> Toasts { get; } = new();

    public void Info(string message, int durationMs = DefaultDurationMs)
        => Show(message, InfoBarSeverity.Informational, durationMs);

    public void Success(string message, int durationMs = DefaultDurationMs)
        => Show(message, InfoBarSeverity.Success, durationMs);

    public void Warning(string message, int durationMs = DefaultDurationMs)
        => Show(message, InfoBarSeverity.Warning, durationMs);

    public void Error(string message, int durationMs = ErrorDurationMs)
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
    public void Dismiss(InAppToast toast) => Remove(toast);

    private void Add(string message, InfoBarSeverity severity, int durationMs)
    {
        // Deduplicate: an identical message+severity toast restarts its timer rather than stacking a
        // second copy (port of the dedup branch in add_toast).
        var existing = Toasts.FirstOrDefault(t => t.Message == message && t.Severity == severity);
        if (existing is not null)
        {
            Remove(existing);
        }

        // Cap the stack: toasts now occupy a real row in the shell, so an unbounded burst (error +
        // reconnect + update, say) would squeeze the page content instead of just overlaying it.
        while (Toasts.Count >= MaxToasts)
        {
            Remove(Toasts[0]);
        }

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
            Remove(toast);
        };
        _timers[toast] = timer;
        timer.Start();
    }

    private void Remove(InAppToast toast)
    {
        if (_timers.Remove(toast, out var timer))
        {
            timer.Stop();
        }
        Toasts.Remove(toast);
    }
}
