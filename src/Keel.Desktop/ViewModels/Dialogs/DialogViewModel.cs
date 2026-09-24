using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>Base class of in-window dialogs shown by <see cref="Services.DialogService"/>.</summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Dialog title.</summary>
    public abstract string Title { get; }

    /// <summary>Completes with true (confirmed) or false (cancelled) when the dialog closes.</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>A validation or save error shown in the dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Widest the dialog layer lets this dialog be (wide dialogs such as the import preview raise it).</summary>
    public virtual double PreferredMaxWidth => 560;

    /// <summary>Whether a save is running.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Closes the dialog.</summary>
    public void Close(bool confirmed) => _completion.TrySetResult(confirmed);

    /// <summary>Validates and saves; returns true to close.</summary>
    protected abstract Task<bool> ConfirmCoreAsync();

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            if (await ConfirmCoreAsync())
            {
                Close(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
