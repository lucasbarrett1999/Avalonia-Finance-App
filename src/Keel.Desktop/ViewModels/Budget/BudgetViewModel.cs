using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Undo;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Budget;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The monthly budget grid (PRD 9.3, F-BUD-1..5). Loads the ledger-derived data for a range of
/// months once (<see cref="IBudgetService.LoadLedgerAsync"/>), computes every month of the range,
/// and switches months in memory. <see cref="BudgetChanged"/> recomputes from the loaded ledger
/// data (assignments and targets only); <see cref="LedgerChanged"/> reloads it. All loads run
/// through one pump on the UI thread, so bursts of messages coalesce and results apply in order.
/// </summary>
public sealed partial class BudgetViewModel : PageViewModel, INavigationTarget, IRecipient<BudgetChanged>, IRecipient<LedgerChanged>
{
    /// <summary>Months loaded before the shown month.</summary>
    public const int MonthsBack = 12;

    /// <summary>Months loaded after the shown month.</summary>
    public const int MonthsAhead = 3;

    private readonly IBudgetService _budget;
    private readonly ICategoryService _categories;
    private readonly IUndoService _undo;
    private readonly INavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private readonly List<BudgetGroupRowViewModel> _groups = [];
    private readonly HashSet<Guid> _collapsed = [];
    private Dictionary<DateOnly, BudgetMonthDto> _months = [];
    private BudgetLedgerData? _ledger;
    private bool _ledgerDirty = true;
    private bool _numbersDirty;
    private bool _pumping;
    private bool _isActive;
    private Task _writes = Task.CompletedTask;

    /// <summary>Creates the view model.</summary>
    public BudgetViewModel(
        IBudgetService budget,
        ICategoryService categories,
        IAccountService accounts,
        IUndoService undo,
        INavigationService navigation,
        DialogService dialogs,
        StatusService status,
        PlatformShortcuts shortcuts,
        TimeProvider time,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(messenger);
        _budget = budget;
        _categories = categories;
        _undo = undo;
        _navigation = navigation;
        _dialogs = dialogs;
        _status = status;
        _time = time;
        Shortcuts = shortcuts;
        CurrentMonth = Today;
        PickerYear = CurrentMonth.Year;
        Inspector = new BudgetInspectorViewModel(this, budget, categories, accounts);
        _undo.Changed += (_, _) => Dispatcher.UIThread.Post(() => UndoCommand.NotifyCanExecuteChanged());
        _navigation.Navigated += (_, e) => _isActive = ReferenceEquals(e.ViewModel, this);
        messenger.Register<BudgetChanged>(this);
        messenger.Register<LedgerChanged>(this);
    }

    /// <summary>Raised when the Assigned editor of a row should receive focus.</summary>
    public event EventHandler<BudgetCategoryRowViewModel>? EditStarted;

    /// <summary>Raised when keyboard focus should return to the grid.</summary>
    public event EventHandler? FocusGridRequested;

    /// <summary>Raised when a row should be scrolled into view.</summary>
    public event EventHandler<BudgetRowViewModel>? ScrollIntoViewRequested;

    /// <summary>Platform shortcuts (key handling and tooltips).</summary>
    public PlatformShortcuts Shortcuts { get; }

    /// <inheritdoc />
    public override string Title => Strings.Page_Budget_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Budget_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Budget_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Budget_EmptyMessage;

    /// <summary>The month shown (first day).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(IsThisMonth))]
    public partial DateOnly CurrentMonth { get; private set; }

    /// <summary>"September 2026".</summary>
    public string MonthTitle => BudgetText.Month(CurrentMonth);

    /// <summary>Whether the shown month is the current calendar month.</summary>
    public bool IsThisMonth => CurrentMonth == Today;

    /// <summary>The numbers of the shown month.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyToAssign), nameof(ReadyToAssignText), nameof(IsReadyToAssignNegative), nameof(TotalsText),
        nameof(AssignedInFutureText), nameof(HasAssignedInFuture), nameof(UncategorizedText), nameof(HasUncategorized), nameof(Currency))]
    public partial BudgetMonthDto? Month { get; private set; }

    /// <summary>Budget currency.</summary>
    public string Currency => Month?.ReadyToAssign.Currency ?? Keel.Domain.Currency.Default;

    /// <summary>Ready to Assign in minor units.</summary>
    public long ReadyToAssign => Month?.ReadyToAssign.Amount ?? 0;

    /// <summary>Ready to Assign text.</summary>
    public string ReadyToAssignText => Month is { } m ? BudgetText.Money(m.ReadyToAssign) : string.Empty;

    /// <summary>Negative Ready to Assign: red pill and the banner (F-BUD-2).</summary>
    public bool IsReadyToAssignNegative => ReadyToAssign < 0;

    /// <summary>"Assigned $X · Activity $Y · Available $Z" (6.4.3).</summary>
    public string TotalsText => Month is { } m
        ? LedgerText.Format(Strings.Budget_Totals, BudgetText.Money(m.TotalAssigned), BudgetText.Money(m.TotalActivity), BudgetText.Money(m.TotalAvailable))
        : string.Empty;

    /// <summary>Money assigned in later months (already subtracted from Ready to Assign).</summary>
    public string AssignedInFutureText => Month is { } m ? LedgerText.Format(Strings.Budget_AssignedInFuture, BudgetText.Money(m.AssignedInFuture)) : string.Empty;

    /// <summary>Whether later months have assignments.</summary>
    public bool HasAssignedInFuture => Month is { AssignedInFuture.Amount: not 0 };

    /// <summary>Activity without a category this month.</summary>
    public string UncategorizedText => Month is { } m ? LedgerText.Format(Strings.Budget_Uncategorized, BudgetText.Money(m.UncategorizedActivity)) : string.Empty;

    /// <summary>Whether this month has uncategorized activity.</summary>
    public bool HasUncategorized => Month is { UncategorizedActivity.Amount: not 0 };

    /// <summary>The visible rows: group rows, and category rows of expanded groups.</summary>
    public ObservableCollection<BudgetRowViewModel> Rows { get; } = [];

    /// <summary>Group rows (expanded or not).</summary>
    public IReadOnlyList<BudgetGroupRowViewModel> Groups => _groups;

    /// <summary>The row holding the cell cursor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCategory))]
    public partial BudgetRowViewModel? SelectedRow { get; private set; }

    /// <summary>The selected category row, if a category is selected.</summary>
    public BudgetCategoryRowViewModel? SelectedCategory => SelectedRow as BudgetCategoryRowViewModel;

    /// <summary>The column of the cell cursor.</summary>
    [ObservableProperty]
    public partial BudgetColumn SelectedColumn { get; private set; } = BudgetColumn.Assigned;

    /// <summary>The row whose Assigned cell is being edited.</summary>
    public BudgetCategoryRowViewModel? EditingRow { get; private set; }

    /// <summary>First load in progress (or loading a month outside the loaded range).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowEmptyState), nameof(ShowLoading), nameof(ShowHeader))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowGrid), nameof(ShowEmptyState), nameof(ShowLoading), nameof(ShowHeader))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>No user categories exist yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowEmptyState))]
    public partial bool HasNoCategories { get; private set; }

    /// <summary>Whether the grid is shown.</summary>
    public bool ShowGrid => !IsLoading && !HasError && !HasNoCategories;

    /// <summary>Whether the designed empty state is shown.</summary>
    public bool ShowEmptyState => !IsLoading && !HasError && HasNoCategories;

    /// <summary>Whether the loading state is shown.</summary>
    public bool ShowLoading => IsLoading && !HasError;

    /// <summary>Whether the month header (Ready to Assign, totals) is shown.</summary>
    public bool ShowHeader => Month is not null && !HasError;

    /// <summary>Whether the right inspector panel is open (I).</summary>
    [ObservableProperty]
    public partial bool IsInspectorOpen { get; set; } = true;

    /// <summary>The inspector panel.</summary>
    public BudgetInspectorViewModel Inspector { get; }

    /// <summary>The latest load (tests await it through <see cref="WhenIdleAsync"/>).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Year shown by the jump-to-month picker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerMonths))]
    public partial int PickerYear { get; set; }

    /// <summary>The twelve months of <see cref="PickerYear"/>.</summary>
    public IReadOnlyList<MonthChoice> PickerMonths => Enumerable.Range(1, 12)
        .Select(m => new MonthChoice(new DateOnly(PickerYear, m, 1), new DateOnly(PickerYear, m, 1) == CurrentMonth))
        .ToList();

    /// <summary>Tooltip of the previous-month button.</summary>
    public string PreviousMonthTip => $"{Strings.Budget_PreviousMonth} ({Shortcuts.Format(Shortcuts.PreviousMonth)})";

    /// <summary>Tooltip of the next-month button.</summary>
    public string NextMonthTip => $"{Strings.Budget_NextMonth} ({Shortcuts.Format(Shortcuts.NextMonth)})";

    /// <summary>Tooltip of Fund targets.</summary>
    public string FundTargetsTip => $"{Strings.Budget_FundTargetsTip} ({Shortcuts.Format(Shortcuts.FundTargets)})";

    /// <summary>Tooltip of Move money.</summary>
    public string MoveMoneyTip => $"{Strings.Budget_MoveMoneyTip} ({Shortcuts.Format(Shortcuts.MoveMoney)})";

    /// <summary>Tooltip of the inspector toggle.</summary>
    public string InspectorTip => $"{Strings.Budget_InspectorTip} ({Shortcuts.Format(Shortcuts.ToggleInspector)})";

    /// <summary>Tooltip of Undo.</summary>
    public string UndoTip => $"{Strings.Shell_Undo} ({Shortcuts.Format(Shortcuts.Undo)})";

    /// <summary>Starter templates for the empty state (F-BUD-8).</summary>
    public IReadOnlyList<BudgetTemplate> Templates { get; } = BudgetTemplate.All;

    private DateOnly Today => BudgetMonth.Of(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        _isActive = true;
        if (parameter is DateOnly month)
        {
            CurrentMonth = BudgetMonth.Of(month);
        }

        if (_ledger is null || _ledgerDirty || _numbersDirty || !_ledger.Covers(CurrentMonth))
        {
            Invalidate(ledger: _ledger is null || _ledgerDirty || !_ledger.Covers(CurrentMonth));
        }
        else
        {
            Apply();
        }

        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Receive(BudgetChanged message) => Dispatcher.UIThread.Post(() => Invalidate(ledger: false));

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(() => Invalidate(ledger: true));

    /// <summary>Waits until loads, refreshes and queued writes have finished (tests).</summary>
    public async Task WhenIdleAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            var loading = Loading;
            var writes = _writes;
            await writes;
            await loading;
            await Inspector.Loading;
            if (ReferenceEquals(loading, Loading) && ReferenceEquals(writes, _writes) && !_pumping)
            {
                return;
            }
        }
    }

    /// <summary>Shows another month; in memory when it is loaded, otherwise loads a range around it.</summary>
    public void GoToMonth(DateOnly month)
    {
        month = BudgetMonth.Of(month);
        if (EditingRow is { } editing)
        {
            CommitEdit(editing, 0, keepEditing: false);
        }

        CurrentMonth = month;
        PickerYear = month.Year;
        if (_ledger is not null && _ledger.Covers(month) && _months.ContainsKey(month))
        {
            Apply();
        }
        else
        {
            IsLoading = true;
            Invalidate(ledger: true);
        }
    }

    /// <summary>Selects a cell and moves the cursor there.</summary>
    public void Select(BudgetRowViewModel? row, BudgetColumn column)
    {
        if (SelectedRow is { } old && !ReferenceEquals(old, row))
        {
            old.SelectedColumn = null;
        }

        SelectedColumn = column;
        SelectedRow = row;
        if (row is not null)
        {
            row.SelectedColumn = column;
            ScrollIntoViewRequested?.Invoke(this, row);
        }

        Inspector.Show(row as BudgetCategoryRowViewModel);
    }

    /// <summary>Moves the cell cursor (arrow keys).</summary>
    public void MoveCursor(int rows, int columns)
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var index = SelectedRow is null ? -1 : Rows.IndexOf(SelectedRow);
        var next = index < 0 ? 0 : Math.Clamp(index + rows, 0, Rows.Count - 1);
        var column = (BudgetColumn)Math.Clamp((int)SelectedColumn + columns, 0, (int)BudgetColumn.Available);
        Select(Rows[next], column);
    }

    /// <summary>Enter on the selected cell: edit Assigned, open Activity, move money from Available, or toggle a group.</summary>
    public void ActivateCell()
    {
        switch (SelectedRow)
        {
            case BudgetGroupRowViewModel group:
                ToggleGroup(group);
                break;
            case BudgetCategoryRowViewModel row when SelectedColumn == BudgetColumn.Activity:
                OpenActivity(row);
                break;
            case BudgetCategoryRowViewModel row when SelectedColumn == BudgetColumn.Available:
                _ = MoveMoneyAsync(row.Id);
                break;
            case BudgetCategoryRowViewModel row:
                BeginEdit(row);
                break;
        }
    }

    /// <summary>Puts the Assigned cell of <paramref name="row"/> in edit mode.</summary>
    public void BeginEdit(BudgetCategoryRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (EditingRow is { } editing && !ReferenceEquals(editing, row))
        {
            CommitEdit(editing, 0, keepEditing: false);
        }

        Select(row, BudgetColumn.Assigned);
        row.EditValue = row.Assigned;
        row.IsEditing = true;
        EditingRow = row;
        EditStarted?.Invoke(this, row);
    }

    /// <summary>
    /// Commits the edited Assigned value (the editor has already parsed its text into
    /// <see cref="BudgetCategoryRowViewModel.EditValue"/>) and moves <paramref name="move"/> category
    /// rows down (negative: up). With <paramref name="keepEditing"/> the next row opens for editing
    /// (Tab); otherwise the cursor lands on it (Enter).
    /// </summary>
    public void CommitEdit(BudgetCategoryRowViewModel row, int move, bool keepEditing)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsEditing)
        {
            return;
        }

        row.IsEditing = false;
        if (ReferenceEquals(EditingRow, row))
        {
            EditingRow = null;
        }

        var value = row.EditValue;
        var month = CurrentMonth;
        if (value != row.Assigned)
        {
            QueueWrite(() => _budget.AssignAsync(row.Id, month, value, CancellationToken.None));
        }

        var next = move == 0 ? row : NextCategory(row, move) ?? row;
        if (keepEditing && !ReferenceEquals(next, row))
        {
            BeginEdit(next);
            return;
        }

        Select(next, BudgetColumn.Assigned);
        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Leaves edit mode without saving (Esc).</summary>
    public void CancelEdit()
    {
        if (EditingRow is not { } row)
        {
            return;
        }

        row.IsEditing = false;
        row.EditValue = row.Assigned;
        EditingRow = null;
        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets Assigned of a category in the shown month (quick assign, F-BUD-5).</summary>
    public Task AssignAsync(Guid categoryId, long value)
    {
        var month = CurrentMonth;
        return QueueWrite(() => _budget.AssignAsync(categoryId, month, value, CancellationToken.None));
    }

    /// <summary>Collapses or expands a group row.</summary>
    [RelayCommand]
    public void ToggleGroup(BudgetGroupRowViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded)
        {
            _collapsed.Remove(group.Id);
        }
        else
        {
            _collapsed.Add(group.Id);
        }

        Flatten();
        if (SelectedRow is BudgetCategoryRowViewModel { Group: var owner } && ReferenceEquals(owner, group) && !group.IsExpanded)
        {
            Select(group, BudgetColumn.Name);
        }
    }

    /// <summary>Opens the All Accounts register filtered to the category and month (card payments: the card's register).</summary>
    public void OpenActivity(BudgetCategoryRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var search = "date:" + CurrentMonth.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        if (row.CardPayment is { } card)
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(card.CardAccountId, search));
        }
        else
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(null, search, row.Id, row.Name, row.Group.Name));
        }
    }

    /// <summary>Opens the move-money dialog with both sides preset (drag and drop of an Available pill).</summary>
    public Task DropAsync(Guid? from, Guid? to)
    {
        if (from == to)
        {
            return Task.CompletedTask;
        }

        var source = from is { } f ? FindCategory(f) : null;
        var target = to is { } t ? FindCategory(t) : null;
        long amount = source is not null ? Math.Max(0, source.Available) : Math.Max(0, ReadyToAssign);
        if (target is { Available: < 0 } overspent && (amount == 0 || amount > -overspent.Available))
        {
            amount = -overspent.Available;
        }

        return ShowMoveMoneyAsync(from, to, amount);
    }

    /// <summary>Opens the move-money dialog (M) from the selected category (or to it, when it is overspent).</summary>
    [RelayCommand]
    public Task MoveMoneyAsync(Guid? categoryId = null)
    {
        var row = categoryId is { } id ? FindCategory(id) : SelectedCategory;
        return row switch
        {
            null => ShowMoveMoneyAsync(null, null, 0),
            { Available: < 0 } => ShowMoveMoneyAsync(null, row.Id, -row.Available),
            _ => ShowMoveMoneyAsync(row.Id, null, Math.Max(0, row.Available)),
        };
    }

    /// <summary>Assigns what every underfunded target needs this month (Ctrl/Cmd+Shift+F).</summary>
    [RelayCommand]
    public async Task FundTargetsAsync()
    {
        var month = CurrentMonth;
        FundTargetsResult? result = null;
        await QueueWrite(async () => result = await _budget.FundTargetsAsync(month, CancellationToken.None));
        if (result is null)
        {
            return;
        }

        if (result.CategoriesFunded == 0 && result.FullyFunded)
        {
            _status.Show(Strings.Budget_FundNothing);
        }
        else if (result.FullyFunded)
        {
            _status.Show(LedgerText.Format(Strings.Budget_FundDone, BudgetText.Money(result.Funded), result.CategoriesFunded), offerUndo: true);
        }
        else
        {
            _status.Show(LedgerText.Format(Strings.Budget_FundShort, BudgetText.Money(result.Funded), BudgetText.Money(result.Shortfall)), offerUndo: result.CategoriesFunded > 0, isError: result.CategoriesFunded == 0);
        }
    }

    /// <summary>Opens the inspector on the target editor of the selected category (T).</summary>
    [RelayCommand]
    public void SetTarget()
    {
        if (SelectedCategory is not { } row)
        {
            return;
        }

        IsInspectorOpen = true;
        Inspector.Show(row);
        _ = FocusTargetWhenLoadedAsync();
    }

    // The editor fields are filled by the inspector's load; focus them afterwards so typing is kept.
    private async Task FocusTargetWhenLoadedAsync()
    {
        await Inspector.Loading;
        Inspector.RequestTargetFocus();
    }

    /// <summary>Shows or hides the inspector (I).</summary>
    [RelayCommand]
    public void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    /// <summary>Shows "How is Ready to Assign computed?" in the inspector.</summary>
    [RelayCommand]
    public void ExplainReadyToAssign()
    {
        Select(null, SelectedColumn);
        IsInspectorOpen = true;
    }

    /// <summary>Opens the quick-assign palette for the selected category (F-BUD-5).</summary>
    [RelayCommand]
    public async Task QuickAssignPaletteAsync()
    {
        if (SelectedCategory is not { } row || _ledger is not { } ledger)
        {
            return;
        }

        QuickAssignDto values;
        try
        {
            values = await Task.Run(() => _budget.GetQuickAssignAsync(ledger, row.Id, CurrentMonth, CancellationToken.None));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            _status.Show(ex.Message, isError: true);
            return;
        }

        var palette = new QuickAssignPaletteViewModel(row.Name, BudgetInspectorViewModel.QuickOptions(values));
        if (await _dialogs.ShowAsync(palette) && palette.Selected is { } choice)
        {
            await AssignAsync(row.Id, choice.Value);
            _status.Show(LedgerText.Format(Strings.Budget_QuickAssigned, row.Name, choice.ValueText), offerUndo: true);
        }

        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies one quick-assign action to a category (context menu, F-BUD-5).</summary>
    public async Task QuickAssignAsync(BudgetCategoryRowViewModel row, QuickAssignKind kind)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_ledger is not { } ledger)
        {
            return;
        }

        var month = CurrentMonth;
        QuickAssignDto values;
        try
        {
            values = await Task.Run(() => _budget.GetQuickAssignAsync(ledger, row.Id, month, CancellationToken.None));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            _status.Show(ex.Message, isError: true);
            return;
        }

        var value = kind switch
        {
            QuickAssignKind.AssignedLastMonth => values.AssignedLastMonth,
            QuickAssignKind.SpentLastMonth => values.SpentLastMonth,
            QuickAssignKind.AverageAssigned => values.AverageAssigned,
            QuickAssignKind.AverageSpent => values.AverageSpent,
            QuickAssignKind.FundTarget => values.FundTarget,
            _ => values.ResetToZero,
        };
        if (value is not { } amount)
        {
            _status.Show(Strings.Quick_NoTarget);
            return;
        }

        await QueueWrite(() => _budget.AssignAsync(row.Id, month, amount.Amount, CancellationToken.None));
        _status.Show(LedgerText.Format(Strings.Budget_QuickAssigned, row.Name, BudgetText.Money(amount)), offerUndo: true);
    }

    /// <summary>Opens the "Manage categories" dialog (F-BUD-1).</summary>
    [RelayCommand]
    public async Task ManageCategoriesAsync()
    {
        var dialog = new ManageCategoriesDialogViewModel(_categories, _status);
        await dialog.LoadAsync();
        await _dialogs.ShowAsync(dialog);
        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a starter template (F-BUD-8); never deletes anything.</summary>
    [RelayCommand]
    public async Task ApplyTemplateAsync(BudgetTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        try
        {
            var created = await Task.Run(() => _categories.ApplyTemplateAsync(template.Groups, CancellationToken.None));
            _status.Show(LedgerText.Format(Strings.Budget_TemplateApplied, template.Name, created), offerUndo: created > 0);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    /// <summary>Undoes the most recent action (header quick action).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    public async Task UndoAsync()
    {
        try
        {
            if (await _undo.UndoAsync(CancellationToken.None) is { } action)
            {
                _status.Show(LedgerText.Format(Strings.Status_Undone, LedgerText.Action(action)));
            }
        }
        catch (InvalidOperationException ex)
        {
            _status.Show(LedgerText.Format(Strings.Status_UndoFailed, ex.Message), isError: true);
        }
    }

    /// <summary>Previous month (Alt+←).</summary>
    [RelayCommand]
    public void PreviousMonth() => GoToMonth(CurrentMonth.AddMonths(-1));

    /// <summary>Next month (Alt+→).</summary>
    [RelayCommand]
    public void NextMonth() => GoToMonth(CurrentMonth.AddMonths(1));

    /// <summary>The current calendar month.</summary>
    [RelayCommand]
    public void GoToToday() => GoToMonth(Today);

    /// <summary>Jump-to-month picker: a month of <see cref="PickerYear"/>.</summary>
    [RelayCommand]
    public void PickMonth(MonthChoice? choice)
    {
        if (choice is not null)
        {
            GoToMonth(choice.Month);
        }
    }

    /// <summary>Jump-to-month picker: previous year.</summary>
    [RelayCommand]
    public void PickerPreviousYear() => PickerYear--;

    /// <summary>Jump-to-month picker: next year.</summary>
    [RelayCommand]
    public void PickerNextYear() => PickerYear++;

    /// <summary>Retries after a load error.</summary>
    [RelayCommand]
    public void Retry()
    {
        ErrorMessage = null;
        IsLoading = true;
        Invalidate(ledger: true);
    }

    /// <summary>The loaded ledger data (inspector computations).</summary>
    internal BudgetLedgerData? Ledger => _ledger;

    /// <summary>A loaded month, if in range.</summary>
    internal BudgetMonthDto? MonthData(DateOnly month) => _months.GetValueOrDefault(BudgetMonth.Of(month));

    /// <summary>The row of a category, if shown.</summary>
    public BudgetCategoryRowViewModel? FindCategory(Guid id) =>
        _groups.SelectMany(g => g.Children).FirstOrDefault(c => c.Id == id);

    /// <summary>Runs budget writes one after another (so the undo stack follows the user's order); errors go to the status strip.</summary>
    internal Task QueueWrite(Func<Task> write)
    {
        var previous = _writes;
        _writes = RunAfterAsync(previous, write);
        return _writes;
    }

    private async Task RunAfterAsync(Task previous, Func<Task> write)
    {
        await previous;
        try
        {
            await Task.Run(write);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            _status.Show(LedgerText.Format(Strings.Budget_ErrorSaving, ex.Message), isError: true);
        }
    }

    private bool CanUndo() => _undo.CanUndo;

    partial void OnIsInspectorOpenChanged(bool value)
    {
        if (value)
        {
            Inspector.Refresh();
        }
    }

    private async Task ShowMoveMoneyAsync(Guid? from, Guid? to, long amount)
    {
        if (Month is null)
        {
            return;
        }

        var options = new List<MoveMoneyOption> { new(null, Strings.Budget_ReadyToAssign, ReadyToAssignText) };
        options.AddRange(_groups.SelectMany(g => g.Children).Select(c => new MoveMoneyOption(c.Id, c.Name, c.AvailableText)));
        var dialog = new MoveMoneyDialogViewModel(_budget, CurrentMonth, Currency, options, from, to, amount, this);
        if (await _dialogs.ShowAsync(dialog))
        {
            _status.Show(LedgerText.Format(Strings.Budget_Moved, LedgerText.Money(dialog.Amount, Currency), dialog.From?.Label, dialog.To?.Label), offerUndo: true);
        }

        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    private BudgetCategoryRowViewModel? NextCategory(BudgetCategoryRowViewModel row, int move)
    {
        var categories = Rows.OfType<BudgetCategoryRowViewModel>().ToList();
        var index = categories.IndexOf(row);
        var next = index + move;
        return index < 0 || next < 0 || next >= categories.Count ? null : categories[next];
    }

    // Marks what is stale and starts the pump when the page is visible.
    private void Invalidate(bool ledger)
    {
        _ledgerDirty |= ledger;
        _numbersDirty = true;
        if (!_isActive && _ledger is not null)
        {
            return;     // reloaded when the page is shown again
        }

        if (!_pumping)
        {
            _pumping = true;
            Loading = PumpAsync();
        }
    }

    // One loop on the UI thread: reload ledger data when the ledger changed (or the month left the
    // loaded range), recompute the range, apply. Repeats while more changes arrived meanwhile.
    private async Task PumpAsync()
    {
        try
        {
            while (_ledgerDirty || _numbersDirty)
            {
                var reloadLedger = _ledgerDirty || _ledger is null || !_ledger.Covers(CurrentMonth);
                _ledgerDirty = false;
                _numbersDirty = false;
                var ledger = _ledger;
                if (reloadLedger || ledger is null)
                {
                    var (from, to) = RangeFor(CurrentMonth);
                    ledger = await Task.Run(() => _budget.LoadLedgerAsync(from, to, CancellationToken.None));
                }

                var months = await Task.Run(() => _budget.GetRangeAsync(ledger, ledger.From, ledger.To, CancellationToken.None));
                _ledger = ledger;
                _months = months.ToDictionary(m => m.Month);
                if (!_ledger.Covers(CurrentMonth))
                {
                    _ledgerDirty = true;    // the user moved on while loading
                    continue;
                }

                ErrorMessage = null;
                Apply();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = LedgerText.Format(Strings.Budget_ErrorLoading, ex.Message);
        }
        finally
        {
            _pumping = false;
            IsLoading = false;
        }
    }

    private (DateOnly From, DateOnly To) RangeFor(DateOnly month) =>
        _ledger is { } current && current.Covers(month)
            ? (current.From, current.To)          // keep the loaded range so reloads cost the same
            : (BudgetMonth.Add(month, -MonthsBack), BudgetMonth.Add(month, MonthsAhead));

    // Shows CurrentMonth: updates the rows in place when the categories are unchanged (month switch),
    // otherwise rebuilds them, keeping collapsed groups and the selection.
    private void Apply()
    {
        if (!_months.TryGetValue(CurrentMonth, out var month))
        {
            return;
        }

        Month = month;
        var shown = month.Groups
            .Where(g => !g.IsHidden)
            .Select(g => (Group: g, Categories: g.Categories.Where(c => !c.IsHidden).ToList()))
            .Where(g => !g.Group.IsSystem || g.Categories.Count > 0)
            .ToList();
        HasNoCategories = !month.Groups.Any(g => !g.IsSystem && g.Categories.Count > 0);

        var sameShape = _groups.Count == shown.Count && _groups.Zip(shown).All(p =>
            p.First.Id == p.Second.Group.Id && p.First.Children.Select(c => c.Id).SequenceEqual(p.Second.Categories.Select(c => c.Id)));
        if (sameShape)
        {
            foreach (var (row, (group, categories)) in _groups.Zip(shown))
            {
                row.Update(group);
                for (var i = 0; i < categories.Count; i++)
                {
                    row.Children[i].Update(categories[i]);
                }
            }
        }
        else
        {
            var selectedId = SelectedRow?.Id;
            var editingId = EditingRow?.Id;
            _groups.Clear();
            foreach (var (group, categories) in shown)
            {
                var row = new BudgetGroupRowViewModel(group) { IsExpanded = !_collapsed.Contains(group.Id) };
                row.Update(group);
                foreach (var category in categories)
                {
                    var child = new BudgetCategoryRowViewModel(row, category, this);
                    child.Update(category);
                    row.Children.Add(child);
                }

                _groups.Add(row);
            }

            EditingRow = null;
            Flatten();
            var reselect = Rows.FirstOrDefault(r => r.Id == selectedId);
            if (reselect is not null)
            {
                Select(reselect, SelectedColumn);
                if (reselect is BudgetCategoryRowViewModel editable && editable.Id == editingId)
                {
                    BeginEdit(editable);
                }
            }
            else
            {
                SelectedRow = null;
            }
        }

        if (SelectedRow is null && Rows.OfType<BudgetCategoryRowViewModel>().FirstOrDefault() is { } first)
        {
            Select(first, BudgetColumn.Assigned);
        }

        Inspector.Refresh();
    }

    private void Flatten()
    {
        var rows = new List<BudgetRowViewModel>();
        foreach (var group in _groups)
        {
            rows.Add(group);
            if (group.IsExpanded)
            {
                rows.AddRange(group.Children);
            }
        }

        if (Rows.SequenceEqual(rows))
        {
            return;
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }
}

/// <summary>A month in the jump-to-month picker.</summary>
/// <param name="Month">First day of the month.</param>
/// <param name="IsCurrent">Whether it is the month shown.</param>
public sealed record MonthChoice(DateOnly Month, bool IsCurrent)
{
    /// <summary>Short month name.</summary>
    public string Label => BudgetText.ShortMonth(Month);
}
