using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain.Rules;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>
/// The rule editor dialog (F-TXN-4): name, enabled, continue, all/any, every condition and action
/// kind, live validation with <see cref="RuleValidator"/> (errors block saving, warnings do not),
/// "Test rule" against existing transactions, and an option to preview applying the rule to
/// existing transactions after saving.
/// </summary>
public sealed partial class RuleEditorViewModel : DialogViewModel
{
    private readonly IRuleService _rules;
    private readonly RuleDefinition _original;
    private bool _loading = true;

    /// <summary>Creates the editor for a new (prefilled) or existing rule.</summary>
    public RuleEditorViewModel(IRuleService rules, RuleEditorContext context, RuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rule);
        _rules = rules;
        _original = rule;
        Context = context;
        MatchModes = [new(RuleMatchMode.All, Strings.RuleEditor_MatchAll), new(RuleMatchMode.Any, Strings.RuleEditor_MatchAny)];
        Name = rule.Name;
        IsEnabled = rule.IsEnabled;
        ContinueAfterMatch = rule.ContinueAfterMatch;
        MatchMode = MatchModes.First(m => m.Value == rule.Conditions.Match);
        Conditions.CollectionChanged += (_, e) => OnPartsChanged(e.NewItems);
        Actions.CollectionChanged += (_, e) => OnPartsChanged(e.NewItems);
        foreach (var condition in rule.Conditions.Conditions)
        {
            Conditions.Add(new ConditionEditorViewModel(context, condition));
        }

        foreach (var action in rule.Actions.Actions)
        {
            Actions.Add(new ActionEditorViewModel(context, action));
        }

        _loading = false;
        Revalidate();
    }

    /// <inheritdoc />
    public override double PreferredMaxWidth => 760;

    /// <inheritdoc />
    public override string Title => IsNew ? Strings.RuleEditor_NewTitle : Strings.RuleEditor_EditTitle;

    /// <summary>Whether the rule is new (not saved yet).</summary>
    public bool IsNew => _original.Id == Guid.Empty;

    /// <summary>Editor context.</summary>
    public RuleEditorContext Context { get; }

    /// <summary>All / any.</summary>
    public IReadOnlyList<Choice<RuleMatchMode>> MatchModes { get; }

    /// <summary>Conditions.</summary>
    public ObservableCollection<ConditionEditorViewModel> Conditions { get; } = [];

    /// <summary>Actions.</summary>
    public ObservableCollection<ActionEditorViewModel> Actions { get; } = [];

    /// <summary>Name.</summary>
    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>Enabled.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>Keep evaluating later rules after a match.</summary>
    [ObservableProperty]
    public partial bool ContinueAfterMatch { get; set; }

    /// <summary>How conditions combine.</summary>
    [ObservableProperty]
    public partial Choice<RuleMatchMode> MatchMode { get; set; }

    /// <summary>After saving, preview applying the rule to existing transactions.</summary>
    [ObservableProperty]
    public partial bool ApplyToExisting { get; set; }

    /// <summary>Problems not tied to one row (name, "no conditions", ...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneralProblems))]
    public partial IReadOnlyList<RuleProblemViewModel> GeneralProblems { get; private set; } = [];

    /// <summary>Whether general problems exist.</summary>
    public bool HasGeneralProblems => GeneralProblems.Count > 0;

    /// <summary>Whether the validator reports errors (saving is blocked).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasErrors { get; private set; }

    /// <summary>"Test rule" state: results, or null before testing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial RuleTestViewModel? TestResult { get; private set; }

    /// <summary>Whether a test result is shown.</summary>
    public bool HasTestResult => TestResult is not null;

    /// <summary>Whether a test is running.</summary>
    [ObservableProperty]
    public partial bool IsTesting { get; private set; }

    /// <summary>The saved rule after a successful save.</summary>
    public RuleDto? Saved { get; private set; }

    /// <summary>The rule as edited.</summary>
    public RuleDefinition Build() => _original with
    {
        Name = Name?.Trim() ?? string.Empty,
        IsEnabled = IsEnabled,
        ContinueAfterMatch = ContinueAfterMatch,
        Conditions = new RuleConditionSet { Match = MatchMode.Value, Conditions = Conditions.Select(c => c.Build()).ToList() },
        Actions = new RuleActionSet { Actions = Actions.Select(a => a.Build()).ToList() },
    };

    /// <summary>Runs the validator and places each problem on its row.</summary>
    public void Revalidate()
    {
        if (_loading)
        {
            return;
        }

        var problems = RuleValidator.Validate(Build(), Context.Validation);
        HasErrors = problems.Any(p => p.Severity == RuleProblemSeverity.Error);
        var general = new List<RuleProblemViewModel>();
        var byRow = new Dictionary<(string, int), List<string>>();
        var rowErrors = new HashSet<(string, int)>();
        foreach (var problem in problems)
        {
            var match = RowPath().Match(problem.Path);
            if (match.Success)
            {
                var key = (match.Groups[1].Value, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                if (!byRow.TryGetValue(key, out var list))
                {
                    byRow[key] = list = [];
                }

                list.Add(Prefix(problem) + problem.Message);
                if (problem.Severity == RuleProblemSeverity.Error)
                {
                    rowErrors.Add(key);
                }
            }
            else
            {
                general.Add(new RuleProblemViewModel(problem.Severity == RuleProblemSeverity.Error, Prefix(problem) + problem.Message));
            }
        }

        GeneralProblems = general;
        for (var i = 0; i < Conditions.Count; i++)
        {
            Conditions[i].Problem = byRow.TryGetValue(("conditions", i), out var list) ? string.Join("\n", list) : null;
            Conditions[i].ProblemIsError = rowErrors.Contains(("conditions", i));
        }

        for (var i = 0; i < Actions.Count; i++)
        {
            Actions[i].Problem = byRow.TryGetValue(("actions", i), out var list) ? string.Join("\n", list) : null;
            Actions[i].ProblemIsError = rowErrors.Contains(("actions", i));
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        Revalidate();
        if (HasErrors)
        {
            Error = Strings.RuleEditor_FixErrors;
            return false;
        }

        try
        {
            Saved = await _rules.SaveAsync(Build(), CancellationToken.None);
            return true;
        }
        catch (RuleValidationException ex)
        {
            Error = string.Join(" ", ex.Problems.Where(p => p.Severity == RuleProblemSeverity.Error).Select(p => p.Message));
            return false;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => ConfirmCommand.ExecuteAsync(null);

    private bool CanSave() => !HasErrors;

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new ConditionEditorViewModel(Context));

    [RelayCommand]
    private void RemoveCondition(ConditionEditorViewModel? condition)
    {
        if (condition is not null)
        {
            Conditions.Remove(condition);
        }
    }

    [RelayCommand]
    private void AddAction() => Actions.Add(new ActionEditorViewModel(Context));

    [RelayCommand]
    private void RemoveAction(ActionEditorViewModel? action)
    {
        if (action is not null)
        {
            Actions.Remove(action);
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        IsTesting = true;
        try
        {
            var result = await _rules.TestAsync(Build(), 25, CancellationToken.None);
            TestResult = new RuleTestViewModel(result);
        }
        finally
        {
            IsTesting = false;
        }
    }

    partial void OnNameChanged(string? value) => Revalidate();

    partial void OnMatchModeChanged(Choice<RuleMatchMode> value) => Revalidate();

    private void OnPartsChanged(System.Collections.IList? added)
    {
        foreach (var part in added?.OfType<RulePartViewModel>() ?? [])
        {
            part.Attach(Revalidate, editor: this);
        }

        Revalidate();
    }

    private static string Prefix(RuleProblem problem) =>
        problem.Severity == RuleProblemSeverity.Warning ? Strings.RuleEditor_WarningPrefix : string.Empty;

    [GeneratedRegex(@"^(conditions|actions)\[(\d+)\]")]
    private static partial Regex RowPath();
}

/// <summary>A validation message.</summary>
/// <param name="IsError">Error (blocks saving) or warning.</param>
/// <param name="Message">Message.</param>
public sealed record RuleProblemViewModel(bool IsError, string Message);

/// <summary>The "Test rule" result for display.</summary>
public sealed class RuleTestViewModel
{
    /// <summary>Creates the view model.</summary>
    public RuleTestViewModel(RuleTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Summary = LedgerText.Format(Strings.RuleEditor_TestSummary, result.Matched.ToString("N0", CultureInfo.CurrentCulture), result.Examined.ToString("N0", CultureInfo.CurrentCulture));
        Rows = result.Samples.Select(s => new RuleOutcomeRowViewModel(s)).ToList();
        HasMore = result.Matched > Rows.Count;
        MoreText = HasMore ? LedgerText.Format(Strings.RuleEditor_TestMore, Rows.Count) : string.Empty;
    }

    /// <summary>"Matches 12 of 3,402 transactions."</summary>
    public string Summary { get; }

    /// <summary>Newest matches.</summary>
    public IReadOnlyList<RuleOutcomeRowViewModel> Rows { get; }

    /// <summary>Whether more matched than are listed.</summary>
    public bool HasMore { get; }

    /// <summary>"Showing the newest 25."</summary>
    public string MoreText { get; }
}

/// <summary>A transaction row in "Test rule" and in the retroactive preview.</summary>
public sealed class RuleOutcomeRowViewModel
{
    /// <summary>Creates the row.</summary>
    public RuleOutcomeRowViewModel(RuleOutcomePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Preview = preview;
        DateText = preview.Date.ToString("d", CultureInfo.CurrentCulture);
        AmountText = LedgerText.Money(preview.Amount, preview.Currency);
        ChangesText = preview.FieldChanges.Count == 0
            ? Strings.RuleOutcome_NoChange
            : string.Join("; ", preview.FieldChanges.Select(Describe));
    }

    /// <summary>The service row.</summary>
    public RuleOutcomePreview Preview { get; }

    /// <summary>Date.</summary>
    public string DateText { get; }

    /// <summary>Payee.</summary>
    public string Payee => Preview.Payee;

    /// <summary>Account.</summary>
    public string Account => Preview.AccountName;

    /// <summary>Amount.</summary>
    public string AmountText { get; }

    /// <summary>What changes, e.g. "Category: Uncategorized → Groceries".</summary>
    public string ChangesText { get; }

    /// <summary>Describes one field change.</summary>
    public static string Describe(FieldChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var label = Strings.ResourceManager.GetString("RuleField_" + change.Field, Strings.Culture) ?? change.Field.ToString();
        return change.Field switch
        {
            RuleChanges.Approved or RuleChanges.Flagged => label,
            RuleChanges.Tags => LedgerText.Format(Strings.RuleOutcome_Change, label, change.Before ?? Strings.RuleOutcome_None, change.After ?? Strings.RuleOutcome_None),
            RuleChanges.Category => LedgerText.Format(Strings.RuleOutcome_Change, label, change.Before ?? Strings.Review_Uncategorized, change.After ?? Strings.Review_Uncategorized),
            _ => LedgerText.Format(Strings.RuleOutcome_Change, label, change.Before ?? Strings.RuleOutcome_None, change.After ?? Strings.RuleOutcome_None),
        };
    }
}
