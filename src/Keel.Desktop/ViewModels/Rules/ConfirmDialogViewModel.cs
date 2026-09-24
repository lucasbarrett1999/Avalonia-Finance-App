using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>A yes/no confirmation (e.g. "Delete rule?").</summary>
/// <param name="title">Title.</param>
/// <param name="message">Explanation.</param>
/// <param name="confirmLabel">Label of the confirming button.</param>
public sealed class ConfirmDialogViewModel(string title, string message, string confirmLabel) : DialogViewModel
{
    /// <inheritdoc />
    public override string Title => title;

    /// <summary>Explanation.</summary>
    public string Message => message;

    /// <summary>Label of the confirming button.</summary>
    public string ConfirmLabel => confirmLabel;

    /// <inheritdoc />
    protected override Task<bool> ConfirmCoreAsync() => Task.FromResult(true);
}
