using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>
/// The right inspector panel of the budget (PRD 9.3): for the selected category its target editor
/// (F-BUD-4), quick-assign buttons (F-BUD-5), note (F-BUD-7), a six-month history, and "How is
/// Available computed?"; with no category selected, "How is Ready to Assign computed?".
/// </summary>
public sealed partial class BudgetInspectorViewModel : ViewModelBase
{
    /// <summary>Months in the history sparkline.</summary>
    public const int HistoryMonths = 6;

    private readonly BudgetViewModel _page;
    private readonly IBudgetService _budget;
    private readonly ICategoryService _categories;
    private readonly IAccountService _accounts;
    private Guid? _editorCategory;
    private string? _savedNote;
    private DateOnly? _monthNoteMonth;
    private string? _savedMonthNote;
    private int _version;

    /// <summary>Creates the inspector for <paramref name="page"/>.</summary>
    public BudgetInspectorViewModel(BudgetViewModel page, IBudgetService budget, ICategoryService categories, IAccountService accounts)
    {
        _page = page;
        _budget = budget;
        _categories = categories;
        _accounts = accounts;
        TargetTypes = Enum.GetValues<TargetType>().Select(t => new Choice<TargetType>(t, BudgetText.TargetType(t))).ToList();
        SelectedTargetType = TargetTypes[0];
    }

    /// <summary>Raised when the target amount field should receive focus (T).</summary>
    public event EventHandler? TargetFocusRequested;

    /// <summary>The category shown, or null for Ready to Assign.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategory), nameof(IsReadyToAssign), nameof(Title), nameof(ExplanationTitle), nameof(CanHaveTarget))]
    public partial BudgetCategoryRowViewModel? Category { get; private set; }

    /// <summary>Whether a category is shown.</summary>
    public bool IsCategory => Category is not null;

    /// <summary>Whether Ready to Assign is explained.</summary>
    public bool IsReadyToAssign => Category is null;

    /// <summary>Panel heading.</summary>
    public string Title => Category?.Name ?? Strings.Budget_ReadyToAssign;

    /// <summary>"How is Available computed?" or "How is Ready to Assign computed?".</summary>
    public string ExplanationTitle => IsCategory ? Strings.Inspector_HowAvailable : Strings.Inspector_HowReadyToAssign;

    /// <summary>The explained number.</summary>
    [ObservableProperty]
    public partial string TotalText { get; private set; } = string.Empty;

    /// <summary>Breakdown lines; the terms sum to the total (principle 7).</summary>
    public ObservableCollection<ExplanationLineViewModel> Lines { get; } = [];

    /// <summary>Loading or refresh in progress (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>A load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    // ---- Target editor (F-BUD-4) ----

    /// <summary>Whether the category can have a target (not Ready to Assign).</summary>
    public bool CanHaveTarget => IsCategory;

    /// <summary>The four target types.</summary>
    public IReadOnlyList<Choice<TargetType>> TargetTypes { get; }

    /// <summary>Selected target type.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsByDate), nameof(IsDebt), nameof(TargetAmountLabel))]
    public partial Choice<TargetType> SelectedTargetType { get; set; }

    /// <summary>Whether the type needs a date.</summary>
    public bool IsByDate => SelectedTargetType.Value == TargetType.SavingsBalanceByDate;

    /// <summary>Whether the type needs a debt account.</summary>
    public bool IsDebt => SelectedTargetType.Value == TargetType.DebtPayment;

    /// <summary>Label of the amount field for the selected type.</summary>
    public string TargetAmountLabel => IsByDate ? Strings.Inspector_TargetBalance : Strings.Inspector_TargetAmount;

    /// <summary>Target amount in minor units.</summary>
    [ObservableProperty]
    public partial long TargetAmount { get; set; }

    /// <summary>Target date (by-date targets).</summary>
    [ObservableProperty]
    public partial DateTime? TargetDate { get; set; }

    /// <summary>Loan and credit accounts for debt targets.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AccountOption> DebtAccounts { get; private set; } = [];

    /// <summary>Selected debt account.</summary>
    [ObservableProperty]
    public partial AccountOption? SelectedDebtAccount { get; set; }

    /// <summary>Whether the category has a saved target.</summary>
    [ObservableProperty]
    public partial bool HasTarget { get; private set; }

    /// <summary>Progress lines of the saved target this month.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> TargetProgress { get; private set; } = [];

    /// <summary>Validation error of the target editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetError))]
    public partial string? TargetError { get; private set; }

    /// <summary>Whether <see cref="TargetError"/> is set.</summary>
    public bool HasTargetError => !string.IsNullOrEmpty(TargetError);

    // ---- Quick assign (F-BUD-5), note (F-BUD-7), history ----

    /// <summary>Quick-assign actions with their values.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<QuickAssignChoice> QuickAssign { get; private set; } = [];

    /// <summary>The category note.</summary>
    [ObservableProperty]
    public partial string? NoteText { get; set; }

    /// <summary>The note of the shown month (F-BUD-7), shown with Ready to Assign.</summary>
    [ObservableProperty]
    public partial string? MonthNoteText { get; set; }

    /// <summary>"Note for August 2026".</summary>
    [ObservableProperty]
    public partial string MonthNoteLabel { get; private set; } = string.Empty;

    /// <summary>Available at the end of each of the last six months (sparkline).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<long> HistoryValues { get; private set; } = [];

    /// <summary>Labels and values under the sparkline.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryPoint> History { get; private set; } = [];

    /// <summary>Screen-reader summary of the history.</summary>
    public string HistoryDescription => string.Join(", ", History.Select(h => h.Label + " " + h.ValueText));

    /// <summary>Shows a category (null: Ready to Assign).</summary>
    public void Show(BudgetCategoryRowViewModel? category)
    {
        if (ReferenceEquals(Category, category))
        {
            return;
        }

        Category = category;
        Refresh();
    }

    /// <summary>Reloads what the panel shows (after a month switch or a budget change).</summary>
    public void Refresh()
    {
        if (!_page.IsInspectorOpen)
        {
            return;
        }

        var version = ++_version;
        Loading = LoadAsync(version);
    }

    /// <summary>Asks the view to focus the target amount (T).</summary>
    public void RequestTargetFocus() => TargetFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Quick-assign choices from the service values.</summary>
    public static IReadOnlyList<QuickAssignChoice> QuickOptions(QuickAssignDto values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = new List<QuickAssignChoice>
        {
            new(Strings.Quick_AssignedLastMonth, values.AssignedLastMonth.Amount, BudgetText.Money(values.AssignedLastMonth)),
            new(Strings.Quick_SpentLastMonth, values.SpentLastMonth.Amount, BudgetText.Money(values.SpentLastMonth)),
            new(Strings.Quick_AverageAssigned, values.AverageAssigned.Amount, BudgetText.Money(values.AverageAssigned)),
            new(Strings.Quick_AverageSpent, values.AverageSpent.Amount, BudgetText.Money(values.AverageSpent)),
        };
        if (values.FundTarget is { } fund)
        {
            list.Add(new(Strings.Quick_FundTarget, fund.Amount, BudgetText.Money(fund)));
        }

        list.Add(new(Strings.Quick_ResetToZero, values.ResetToZero.Amount, BudgetText.Money(values.ResetToZero)));
        return list;
    }

    /// <summary>Applies a quick-assign value to the category.</summary>
    [RelayCommand]
    public async Task ApplyQuickAssignAsync(QuickAssignChoice? choice)
    {
        if (choice is null || Category is not { } category)
        {
            return;
        }

        await _page.AssignAsync(category.Id, choice.Value);
    }

    /// <summary>Creates or replaces the target.</summary>
    [RelayCommand]
    public async Task SaveTargetAsync()
    {
        if (Category is not { } category)
        {
            return;
        }

        TargetError = null;
        if (TargetAmount <= 0)
        {
            TargetError = Strings.Inspector_TargetAmountRequired;
            return;
        }

        if (IsByDate && TargetDate is null)
        {
            TargetError = Strings.Inspector_TargetDateRequired;
            return;
        }

        if (IsDebt && SelectedDebtAccount is null)
        {
            TargetError = Strings.Inspector_TargetAccountRequired;
            return;
        }

        var target = new TargetDto(
            category.Id,
            SelectedTargetType.Value,
            TargetAmount,
            IsByDate && TargetDate is { } date ? DateOnly.FromDateTime(date) : null,
            IsDebt ? SelectedDebtAccount?.Id : null);
        Exception? failure = null;
        await _page.QueueWrite(async () =>
        {
            try
            {
                await _budget.SetTargetAsync(target, CancellationToken.None);
            }
            catch (ArgumentException ex)
            {
                failure = ex;
            }
        });
        TargetError = failure?.Message;
    }

    /// <summary>Removes the target.</summary>
    [RelayCommand]
    public async Task RemoveTargetAsync()
    {
        if (Category is not { } category)
        {
            return;
        }

        _editorCategory = null;       // reset the editor from the saved state after the change
        await _page.QueueWrite(() => _budget.DeleteTargetAsync(category.Id, CancellationToken.None));
    }

    /// <summary>Saves the note when it changed (on leaving the field).</summary>
    [RelayCommand]
    public async Task SaveNoteAsync()
    {
        if (Category is not { } category || string.Equals((NoteText ?? string.Empty).Trim(), _savedNote ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        _savedNote = string.IsNullOrWhiteSpace(NoteText) ? null : NoteText.Trim();
        await _page.QueueWrite(() => _categories.SetNoteAsync(category.Id, NoteText, CancellationToken.None));
    }

    /// <summary>Saves the month note when it changed (on leaving the field).</summary>
    [RelayCommand]
    public async Task SaveMonthNoteAsync()
    {
        if (_monthNoteMonth is not { } month || string.Equals((MonthNoteText ?? string.Empty).Trim(), _savedMonthNote ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        _savedMonthNote = string.IsNullOrWhiteSpace(MonthNoteText) ? null : MonthNoteText.Trim();
        await _page.QueueWrite(() => _budget.SetMonthNoteAsync(month, MonthNoteText, CancellationToken.None));
    }

    private async Task LoadAsync(int version)
    {
        if (_page.Ledger is not { } ledger)
        {
            return;
        }

        var month = _page.CurrentMonth;
        var category = Category;
        try
        {
            Error = null;
            var explanation = await Task.Run(() => _budget.ExplainAsync(ledger, category?.Id, month, CancellationToken.None));
            if (version != _version)
            {
                return;
            }

            TotalText = BudgetText.Money(explanation.Total);
            Lines.Clear();
            foreach (var line in explanation.Lines)
            {
                Lines.Add(new ExplanationLineViewModel(BudgetText.ExplanationLabel(line), BudgetText.Money(line.Amount), line.IsTerm));
            }

            if (category is null)
            {
                if (_monthNoteMonth != month)
                {
                    await SaveMonthNoteAsync();     // an unsaved note of the previous month
                    var note = await Task.Run(() => _budget.GetMonthNoteAsync(month, CancellationToken.None));
                    if (version != _version)
                    {
                        return;
                    }

                    _monthNoteMonth = month;
                    _savedMonthNote = note;
                    MonthNoteText = note;
                    MonthNoteLabel = LedgerText.Format(Strings.Inspector_MonthNote, BudgetText.Month(month));
                }

                return;
            }

            UpdateHistory(category.Id, month);
            UpdateTargetProgress(category);
            var quick = await Task.Run(() => _budget.GetQuickAssignAsync(ledger, category.Id, month, CancellationToken.None));
            if (version != _version)
            {
                return;
            }

            QuickAssign = QuickOptions(quick);
            if (_editorCategory != category.Id)
            {
                await LoadEditorsAsync(category, version);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            if (version == _version)
            {
                Error = ex.Message;
            }
        }
    }

    // Target editor, debt accounts and note: loaded when another category is shown, so a refresh
    // never overwrites what the user is typing.
    private async Task LoadEditorsAsync(BudgetCategoryRowViewModel category, int version)
    {
        var target = await Task.Run(() => _budget.GetTargetAsync(category.Id, CancellationToken.None));
        var note = await _categories.GetNoteAsync(category.Id, CancellationToken.None);
        var accounts = await _accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        if (version != _version)
        {
            return;
        }

        _editorCategory = category.Id;
        DebtAccounts = accounts.Where(a => a.IsLiability).Select(AccountOption.From).ToList();
        HasTarget = target is not null;
        SelectedTargetType = TargetTypes.First(t => t.Value == (target?.Type ?? TargetType.MonthlySetAside));
        TargetAmount = target?.Amount ?? 0;
        TargetDate = target?.TargetDate?.ToDateTime(TimeOnly.MinValue);
        SelectedDebtAccount = DebtAccounts.FirstOrDefault(a => a.Id == (target?.LinkedAccountId ?? category.CardPayment?.CardAccountId));
        TargetError = null;
        _savedNote = note;
        NoteText = note;
    }

    private void UpdateTargetProgress(BudgetCategoryRowViewModel category)
    {
        if (category.Target is not { } t)
        {
            HasTarget = false;
            TargetProgress = [];
            return;
        }

        HasTarget = true;
        var lines = new List<string>
        {
            BudgetText.TargetSummary(t),
            LedgerText.Format(Strings.Inspector_NeededThisMonth, BudgetText.Money(t.NeededThisMonth)),
            t.Underfunded.IsZero ? Strings.Inspector_TargetMet : LedgerText.Format(Strings.Inspector_Underfunded, BudgetText.Money(t.Underfunded)),
        };
        if (t.Type == TargetType.SavingsBalanceByDate)
        {
            lines.Add(LedgerText.Format(Strings.Inspector_MonthlyNeed, BudgetText.Money(t.MonthlyNeed)));
        }

        TargetProgress = lines;
    }

    private void UpdateHistory(Guid categoryId, DateOnly month)
    {
        var points = new List<HistoryPoint>();
        for (var i = HistoryMonths - 1; i >= 0; i--)
        {
            var m = BudgetMonth.Add(month, -i);
            if (_page.MonthData(m) is not { } data)
            {
                continue;
            }

            var cell = data.Groups.SelectMany(g => g.Categories).FirstOrDefault(c => c.Id == categoryId);
            if (cell is not null)
            {
                points.Add(new HistoryPoint(BudgetText.ShortMonth(m), cell.Available.Amount, BudgetText.Money(cell.Available)));
            }
        }

        History = points;
        HistoryValues = points.Select(p => p.Value).ToList();
        OnPropertyChanged(nameof(HistoryDescription));
    }
}

/// <summary>A line of the "How is it computed?" breakdown.</summary>
/// <param name="Label">What the amount is.</param>
/// <param name="AmountText">Formatted amount.</param>
/// <param name="IsTerm">Whether it is a term of the sum (context lines are shown muted).</param>
public sealed record ExplanationLineViewModel(string Label, string AmountText, bool IsTerm);

/// <summary>A quick-assign action (F-BUD-5).</summary>
/// <param name="Label">Action name.</param>
/// <param name="Value">Value Assigned becomes.</param>
/// <param name="ValueText">Formatted value.</param>
public sealed record QuickAssignChoice(string Label, long Value, string ValueText);

/// <summary>A month in the history sparkline.</summary>
/// <param name="Label">Short month name.</param>
/// <param name="Value">Available at the end of the month.</param>
/// <param name="ValueText">Formatted value.</param>
public sealed record HistoryPoint(string Label, long Value, string ValueText);
