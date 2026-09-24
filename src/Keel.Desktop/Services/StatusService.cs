using CommunityToolkit.Mvvm.ComponentModel;

namespace Keel.Desktop.Services;

/// <summary>
/// The status strip (PRD 9.1): a message and, for undoable actions, an "Undo" action (the toast).
/// </summary>
public sealed partial class StatusService : ObservableObject
{
    /// <summary>Message text.</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    /// <summary>Whether the message offers Undo.</summary>
    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    /// <summary>Whether the message reports a problem.</summary>
    [ObservableProperty]
    public partial bool IsError { get; private set; }

    /// <summary>Shows a message; <paramref name="offerUndo"/> adds the Undo action.</summary>
    public void Show(string message, bool offerUndo = false, bool isError = false)
    {
        Message = message;
        CanUndo = offerUndo;
        IsError = isError;
    }

    /// <summary>Removes the Undo action (e.g. after it was used).</summary>
    public void ClearUndo() => CanUndo = false;
}
