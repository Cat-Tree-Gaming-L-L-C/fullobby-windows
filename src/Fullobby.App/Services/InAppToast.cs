using Microsoft.UI.Xaml.Controls;

namespace Fullobby.App.Services;

/// <summary>A single transient in-app notification rendered as an InfoBar in the shell
/// (info/success/warning/error).</summary>
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
