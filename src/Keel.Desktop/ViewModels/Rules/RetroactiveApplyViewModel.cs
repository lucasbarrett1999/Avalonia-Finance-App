using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>
/// "Apply to existing transactions" (F-TXN-4): previews every transaction the rules would change
/// and the fields each would receive, then applies them as one undoable action.
/// </summary>
public sealed partial class RetroactiveApplyViewModel : DialogViewModel
{
    private readonly IRuleService _rules;
    private readonly IReadOnlyList<Guid> _ruleIds;
    private int _version;

    /// <summary>Creates the dialog and starts the preview.</summary>
    public RetroactiveApplyViewModel(IRuleService rules, IReadOnlyList<Guid> ruleIds, string rulesLabel)
    {
        _rules = rules;
        _ruleIds = ruleIds;
        RulesLabel = rulesLabel;
        Loading = LoadAsync();
    }

    /// <inheritdoc />
    public override double PreferredMaxWidth => 760;

    /// <inheritdoc />
    public override string Title => Strings.Retroactive_Title;

    /// <summary>Which rules, e.g. "Rule: Groceries".</summary>
    public string RulesLabel { get; }

    /// <summary>Only look at transactions still in the review queue.</summary>
    [ObservableProperty]
    public partial bool UnapprovedOnly { get; set; }

    /// <summary>Whether the preview is loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChanges))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Rows that would change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChanges), nameof(CanApply))]
    public partial IReadOnlyList<RuleOutcomeRowViewModel> Changes { get; private set; } = [];

    /// <summary>"12 of 3,402 transactions will change."</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Whether the preview found nothing to change.</summary>
    public bool HasNoChanges => !IsLoading && Changes.Count == 0 && !HasError;

    /// <summary>Whether Apply is available.</summary>
    public bool CanApply => Changes.Count > 0;

    /// <summary>The preview load (tests await it).</summary>
    public Task Loading { get; private set; }

    /// <summary>What was applied, after confirming.</summary>
    public RetroactiveResult? Result { get; private set; }

    /// <summary>The scope the preview and the apply use.</summary>
    public RetroactiveScope Scope => new(UnapprovedOnly: UnapprovedOnly);

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        await Loading;
        if (!CanApply)
        {
            return false;
        }

        try
        {
            Result = await _rules.ApplyRetroactivelyAsync(_ruleIds, Scope, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }

    partial void OnUnapprovedOnlyChanged(bool value) => Loading = LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_version;
        IsLoading = true;
        Error = null;
        try
        {
            var preview = await _rules.PreviewRetroactiveAsync(_ruleIds, Scope, CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            Changes = preview.Changes.Select(c => new RuleOutcomeRowViewModel(c)).ToList();
            Summary = LedgerText.Format(Strings.Retroactive_Summary,
                preview.Changes.Count.ToString("N0", CultureInfo.CurrentCulture),
                preview.Examined.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                Changes = [];
                Error = LedgerText.Format(Strings.Retroactive_Error, ex.Message);
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasNoChanges));
            }
        }
    }
}
