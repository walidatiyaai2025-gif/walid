using System.Windows;
using PCCExecutive.App.Views;

namespace PCCExecutive.App.Services;

public interface IConfirmationService
{
    bool Confirm(string title, string message, string confirmLabel);
}

/// <summary>
/// Premium in-app confirmation for destructive operator actions. The presentation layer
/// never bypasses the runtime ownership/policy gate; this only confirms an already-allowed action.
/// </summary>
public sealed class WpfConfirmationService : IConfirmationService
{
    public bool Confirm(string title, string message, string confirmLabel)
    {
        var dialog = new ConfirmationDialog(title, message, confirmLabel);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }
}

/// <summary>
/// Fail-safe default used by view-model-only hosts/tests unless a UI confirmation service is supplied.
/// </summary>
public sealed class DenyConfirmationService : IConfirmationService
{
    public bool Confirm(string title, string message, string confirmLabel) => false;
}
