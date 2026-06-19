using Microsoft.UI.Xaml.Controls;

namespace ChllSeeding.App.Services;

/// <summary>A single transient in-app notification rendered as an InfoBar in the shell.
/// Port of the Rust <c>state::toast</c> surface (info/success/warning/error).</summary>
public sealed class InAppToast
{
    public InAppToast(string message, InfoBarSeverity severity)
    {
        Message = message;
        Severity = severity;
    }

    public string Message { get; }

    /// <summary>Mapped straight onto <see cref="InfoBar.Severity"/> so the template binds without a converter.</summary>
    public InfoBarSeverity Severity { get; }
}
