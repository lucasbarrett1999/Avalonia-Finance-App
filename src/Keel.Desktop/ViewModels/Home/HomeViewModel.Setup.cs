using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Setup;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Home;

namespace Keel.Desktop.ViewModels;

/// <summary>The "Get started" checklist card (PRD 9.10 step 4): four steps that tick themselves off from the data.</summary>
public sealed partial class HomeViewModel
{
    private readonly ISetupProgressService? _setup;
    private bool _sawIncompleteSetup;

    /// <summary>The checklist steps, in order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChecklist))]
    public partial IReadOnlyList<SetupStepViewModel> SetupSteps { get; private set; } = [];

    /// <summary>"2 of 4 done".</summary>
    [ObservableProperty]
    public partial string SetupProgressText { get; private set; } = string.Empty;

    /// <summary>Completed steps (0–4), for the progress bar.</summary>
    [ObservableProperty]
    public partial int SetupCompleted { get; private set; }

    /// <summary>All four steps are done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChecklist))]
    public partial bool IsSetupComplete { get; private set; }

    /// <summary>The user hid the card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChecklist))]
    public partial bool IsSetupDismissed { get; private set; }

    /// <summary>
    /// Whether the card shows: while steps are open, and after the last one is done in this session (with
    /// "You're set up") until hidden. A file that was already set up never shows it.
    /// </summary>
    public bool ShowChecklist => SetupSteps.Count > 0 && !IsSetupDismissed && (!IsSetupComplete || _sawIncompleteSetup);

    /// <summary>Hides the checklist for this budget file.</summary>
    [RelayCommand]
    public async Task DismissChecklistAsync()
    {
        IsSetupDismissed = true;
        if (_setup is not null)
        {
            await Task.Run(() => _setup.DismissAsync(CancellationToken.None));
        }
    }

    private async Task LoadSetupAsync(int version)
    {
        if (_setup is null)
        {
            return;
        }

        var progress = await _setup.GetAsync(CancellationToken.None);
        if (version != _version)
        {
            return;
        }

        SetupSteps =
        [
            new(Strings.Setup_File, Strings.Setup_FileHint, true, null, Strings.Setup_FileAction),
            new(Strings.Setup_Categories, Strings.Setup_CategoriesHint, progress.HasCategories, AssignCommand, Strings.Setup_CategoriesAction),
            new(Strings.Setup_Account, Strings.Setup_AccountHint, progress.HasAccounts, AddAccountCommand, Strings.Setup_AccountAction),
            new(Strings.Setup_Assign, Strings.Setup_AssignHint, progress.HasAssignments, AssignCommand, Strings.Setup_AssignAction),
        ];
        SetupCompleted = progress.CompletedSteps;
        _sawIncompleteSetup |= !progress.IsComplete;
        IsSetupComplete = progress.IsComplete;
        SetupProgressText = LedgerText.Format(Strings.Setup_Progress, progress.CompletedSteps, 4);
        IsSetupDismissed = progress.IsDismissed;
    }
}
