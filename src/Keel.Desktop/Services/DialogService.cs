using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.Services;

/// <summary>
/// Shows in-window modal dialogs: the shell renders <see cref="Current"/> over a scrim through the
/// view locator, so dialogs work the same on every platform and in headless tests.
/// </summary>
public sealed partial class DialogService : ObservableObject
{
    /// <summary>The dialog on screen, or null.</summary>
    [ObservableProperty]
    public partial DialogViewModel? Current { get; private set; }

    /// <summary>Shows <paramref name="dialog"/> and completes when it closes; true when confirmed.</summary>
    public async Task<bool> ShowAsync(DialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Current?.Close(false);
        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
            {
                Current = null;
            }
        }
    }
}
