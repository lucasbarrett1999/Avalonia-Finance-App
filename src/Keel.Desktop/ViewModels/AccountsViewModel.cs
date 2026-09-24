using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Payees;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.ViewModels.Rules;
using Keel.Domain;
using Keel.Domain.Ledger;

namespace Keel.Desktop.ViewModels;

/// <summary>Where to open the register: one account (or all), optionally with a search and a category filter.</summary>
/// <param name="AccountId">Account, or null for All Accounts.</param>
/// <param name="Search">Search text in the F-TXN-7 syntax.</param>
/// <param name="Category">Category filter to apply (report drill-down; <see cref="CategoryOption.All"/> clears it); other filters are reset.</param>
public sealed record RegisterNavigation(Guid? AccountId, string? Search = null, CategoryOption? Category = null);

/// <summary>
/// The account register and the "All accounts" register (PRD 9.4, F-ACC-2..5): header balances,
/// filter bar, a virtualized grid over the paged database source, the inline editor, bulk actions,
/// and the reconcile bar. Navigate with a <see cref="Guid"/> (one account), a
/// <see cref="RegisterNavigation"/>, or null (all accounts).
/// </summary>
public sealed partial class AccountsViewModel : PageViewModel, INavigationTarget, IRecipient<LedgerChanged>
{
    private readonly IRegisterQuery _register;
    private readonly ITransactionService _transactions;
    private readonly IAccountService _accounts;
    private readonly IPayeeService _payees;
    private readonly ICategoryService _categories;
    private readonly IBalanceSnapshotService _snapshots;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly ImportWorkflow _import;
    private IReadOnlyList<RegisterRowViewModel> _selection = [];
    private RegisterSort _sort = RegisterSort.Default;
    private Guid? _selectAfterRefresh;
    private int _loadVersion;
    private bool _suppressFilterReload;

    /// <summary>Creates the view model.</summary>
    public AccountsViewModel(
        IRegisterQuery register,
        ITransactionService transactions,
        IAccountService accounts,
        IPayeeService payees,
        ICategoryService categories,
        IBalanceSnapshotService snapshots,
        DialogService dialogs,
        StatusService status,
        IMessenger messenger,
        ImportWorkflow import,
        RuleEditorFlow ruleEditor)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _import = import;
        _ruleEditor = ruleEditor;
        _register = register;
        _transactions = transactions;
        _accounts = accounts;
        _payees = payees;
        _categories = categories;
        _snapshots = snapshots;
        _dialogs = dialogs;
        _status = status;
        Rows = new RegisterSource((skip, take, ct) => _register.GetPageAsync(CurrentFilter(), _sort, skip, take, ct));
        Rows.SortRequested += (_, sort) =>
        {
            _sort = sort;
            _ = ReloadRowsAsync();
        };
        Rows.PageFailed += (_, ex) => ErrorMessage = LedgerText.Format(Strings.Register_ErrorLoading, ex.Message);
        DatePresets = Enum.GetValues<DatePreset>().Select(p => new Choice<DatePreset>(p, Label("DatePreset_" + p))).ToList();
        StatusFilters = Enum.GetValues<StatusFilter>().Select(p => new Choice<StatusFilter>(p, Label("StatusFilter_" + p))).ToList();
        SelectedDatePreset = DatePresets[0];
        SelectedStatusFilter = StatusFilters[0];
        SelectedCategoryFilter = CategoryOption.All;
        messenger.Register(this);
    }

    /// <summary>Raised when the view should select and scroll to a row index.</summary>
    public event EventHandler<int>? SelectIndexRequested;

    /// <summary>Raised when the view should move focus into the editor.</summary>
    public event EventHandler? EditorOpened;

    /// <summary>Raised when the view should move focus back to the grid.</summary>
    public event EventHandler? EditorClosed;

    /// <summary>The account shown, or null for All Accounts.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>The account shown, once loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(IsTracking), nameof(CanReconcile), nameof(IsAccountClosed), nameof(CanImportFile))]
    public partial AccountDto? Account { get; private set; }

    /// <summary>The virtual row list bound to the grid.</summary>
    public RegisterSource Rows { get; }

    /// <summary>Whether this is the All Accounts register (adds the Account column).</summary>
    public bool IsAllAccounts => AccountId is null;

    /// <summary>Whether the account is a tracking account (shows snapshots).</summary>
    public bool IsTracking => Account is { IsOnBudget: false };

    /// <summary>Whether the account is closed.</summary>
    public bool IsAccountClosed => Account?.IsClosed ?? false;

    /// <summary>Whether the reconcile action applies.</summary>
    public bool CanReconcile => Account is { IsClosed: false };

    /// <inheritdoc />
    public override string Title => Account?.Name ?? Strings.Page_Accounts_Title;

    /// <inheritdoc />
    public override string Subtitle => Account is { } a
        ? LedgerText.Format(Strings.Register_Subtitle, LedgerText.AccountType(a.Type), LedgerText.Group(a.Group))
        : Strings.Page_Accounts_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => HasNoAccounts ? Strings.Page_Accounts_EmptyHeading
        : HasActiveFilters ? Strings.Register_FilteredEmptyHeading
        : Strings.Register_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => HasNoAccounts ? Strings.Page_Accounts_EmptyMessage
        : HasActiveFilters ? Strings.Register_FilteredEmptyMessage
        : Strings.Register_EmptyMessage;

    /// <summary>Header balances.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LedgerBalanceText), nameof(ClearedText), nameof(UnclearedText), nameof(ReportedText), nameof(HasReported), nameof(ReportedMismatch))]
    public partial RegisterSummary? Summary { get; private set; }

    /// <summary>Latest balance snapshot text (tracking accounts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot))]
    public partial string? SnapshotText { get; private set; }

    /// <summary>Whether a snapshot exists.</summary>
    public bool HasSnapshot => !string.IsNullOrEmpty(SnapshotText);

    /// <summary>Ledger ("working") balance text.</summary>
    public string LedgerBalanceText => Summary is { } s ? LedgerText.Money(s.Ledger, s.Currency) : string.Empty;

    /// <summary>Cleared balance text.</summary>
    public string ClearedText => Summary is { } s ? LedgerText.Money(s.Cleared, s.Currency) : string.Empty;

    /// <summary>Uncleared balance text.</summary>
    public string UnclearedText => Summary is { } s ? LedgerText.Money(s.Uncleared, s.Currency) : string.Empty;

    /// <summary>Reported balance text.</summary>
    public string ReportedText => Summary is { ReportedBalance: { } r } s ? LedgerText.Money(r, s.Currency) : string.Empty;

    /// <summary>Whether the account has a provider-reported balance.</summary>
    public bool HasReported => Summary?.ReportedBalance is not null;

    /// <summary>Whether the reported balance differs from the cleared balance (hint to reconcile).</summary>
    public bool ReportedMismatch => Summary is { ReportedBalance: { } r } s && r != s.Cleared;

    /// <summary>Total rows matching the filters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowCountText), nameof(ShowGrid), nameof(ShowEmptyState), nameof(EmptyHeading), nameof(EmptyMessage))]
    public partial int RowCount { get; private set; }

    /// <summary>"12,345 transactions".</summary>
    public string RowCountText => LedgerText.Format(Strings.Register_RowCount, RowCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>First load in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowEmptyState))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowGrid), nameof(ShowEmptyState))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>All Accounts with no accounts at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyHeading), nameof(EmptyMessage), nameof(ShowGrid), nameof(ShowEmptyState), nameof(CanAddTransaction), nameof(CanImportFile))]
    public partial bool HasNoAccounts { get; private set; }

    /// <summary>Whether the grid is shown.</summary>
    public bool ShowGrid => !IsLoading && !HasError && RowCount > 0;

    /// <summary>Whether the designed empty state is shown.</summary>
    public bool ShowEmptyState => !IsLoading && !HasError && RowCount == 0;

    /// <summary>Whether a transaction can be added here.</summary>
    public bool CanAddTransaction => !HasNoAccounts && !IsAccountClosed;

    /// <summary>Whether "Import file" applies (F-TXN-2): an open account, or All Accounts with accounts.</summary>
    public bool CanImportFile => !HasNoAccounts && !IsAccountClosed;

    /// <summary>Filter: search text (F-TXN-7 syntax).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(SearchErrorText))]
    public partial string? SearchText { get; set; }

    /// <summary>Tokens of the search that could not be parsed.</summary>
    public string? SearchErrorText => SearchQuery.Parse(SearchText).Errors is { Count: > 0 } errors
        ? LedgerText.Format(Strings.Register_SearchIgnored, string.Join(" ", errors))
        : null;

    /// <summary>Date presets.</summary>
    public IReadOnlyList<Choice<DatePreset>> DatePresets { get; }

    /// <summary>Selected date preset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial Choice<DatePreset> SelectedDatePreset { get; set; }

    /// <summary>Status filters.</summary>
    public IReadOnlyList<Choice<StatusFilter>> StatusFilters { get; }

    /// <summary>Selected status filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial Choice<StatusFilter> SelectedStatusFilter { get; set; }

    /// <summary>Category filter options ("All categories" first).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CategoryOption> CategoryFilters { get; private set; } = [CategoryOption.All];

    /// <summary>Selected category filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial CategoryOption SelectedCategoryFilter { get; set; }

    /// <summary>Only unapproved rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial bool UnapprovedOnly { get; set; }

    /// <summary>Whether any filter is active.</summary>
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || (SelectedDatePreset?.Value ?? DatePreset.AllDates) != DatePreset.AllDates
        || (SelectedStatusFilter?.Value ?? StatusFilter.All) != StatusFilter.All || SelectedCategoryFilter?.Id is not null || UnapprovedOnly;

    /// <summary>The inline editor, when open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial TransactionEditorViewModel? Editor { get; private set; }

    /// <summary>Whether the editor is open.</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>The reconcile bar, when open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconciling))]
    public partial ReconcileViewModel? Reconcile { get; private set; }

    /// <summary>Whether the reconcile bar is open.</summary>
    public bool IsReconciling => Reconcile is not null;

    /// <summary>Number of selected rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))]
    public partial int SelectedCount { get; private set; }

    /// <summary>Whether rows are selected.</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>"3 selected".</summary>
    public string SelectionText => LedgerText.Format(Strings.Register_Selected, SelectedCount);

    /// <summary>The current load or refresh (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Open accounts for pickers.</summary>
    public IReadOnlyList<AccountOption> AccountOptions { get; private set; } = [];

    /// <summary>Assignable categories for pickers.</summary>
    public IReadOnlyList<CategoryOption> CategoryOptions { get; private set; } = [];

    /// <summary>The currently selected rows.</summary>
    public IReadOnlyList<RegisterRowViewModel> Selection => _selection;

    /// <summary>Waits until the current load, refresh and page fetches are done.</summary>
    public async Task WhenIdleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var loading = Loading;
            await loading;
            await Rows.WhenIdle;
            if (Reconcile is { } reconcile)
            {
                await reconcile.RefreshAsync();
            }

            if (ReferenceEquals(loading, Loading))
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        var (accountId, search) = parameter switch
        {
            Guid id => ((Guid?)id, (string?)null),
            RegisterNavigation nav => (nav.AccountId, nav.Search),
            _ => (null, null),
        };

        var sameTarget = accountId == AccountId && Account is not null == accountId is not null;
        AccountId = accountId;
        Editor = null;
        Reconcile = null;
        _suppressFilterReload = true;
        if (!sameTarget)
        {
            Account = null;
            SelectedDatePreset = DatePresets[0];
            SelectedStatusFilter = StatusFilters[0];
            SelectedCategoryFilter = CategoryOption.All;
            UnapprovedOnly = false;
            _sort = RegisterSort.Default;
            Rows.SortDescriptions.Clear();
        }

        SearchText = search ?? (sameTarget ? SearchText : null);
        if (parameter is RegisterNavigation { Category: { } category })
        {
            SelectedDatePreset = DatePresets[0];
            SelectedStatusFilter = StatusFilters[0];
            UnapprovedOnly = false;
            SelectedCategoryFilter = CategoryFilters.FirstOrDefault(f => f.Id == category.Id) ?? category;
        }

        _suppressFilterReload = false;
        OnPropertyChanged(nameof(IsAllAccounts));
        Loading = LoadAsync(showSpinner: true);
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Dispatcher.UIThread.Post(() =>
        {
            if (AccountId is { } id && message.AccountIds.Count > 0 && !message.AccountIds.Contains(id))
            {
                return;
            }

            Loading = LoadAsync(showSpinner: false);
        });
    }

    /// <summary>Called by the view when the grid selection changes.</summary>
    public void SetSelection(IReadOnlyList<RegisterRowViewModel> rows)
    {
        _selection = rows.Where(r => r.IsLoaded).ToList();
        SelectedCount = _selection.Count;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the editor for a new transaction (N).</summary>
    [RelayCommand]
    public async Task NewTransactionAsync()
    {
        if (!CanAddTransaction)
        {
            return;
        }

        await Loading;
        var open = AccountOptions.Where(a => !a.IsClosed).ToList();
        var account = AccountId is { } id ? open.FirstOrDefault(a => a.Id == id) : open.FirstOrDefault();
        Editor = new TransactionEditorViewModel(_payees, open, CategoryOptions, account, existing: null, canChooseAccount: IsAllAccounts);
        EditorOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the editor for the selected row (Enter).</summary>
    [RelayCommand]
    public async Task EditSelectedAsync()
    {
        if (_selection.Count != 1)
        {
            return;
        }

        var dto = await _transactions.GetAsync(_selection[0].Id, CancellationToken.None);
        if (dto is null)
        {
            return;
        }

        var choices = AccountOptions.Where(a => !a.IsClosed || a.Id == dto.AccountId || a.Id == dto.TransferAccountId).ToList();
        var account = choices.FirstOrDefault(a => a.Id == dto.AccountId);
        Editor = new TransactionEditorViewModel(_payees, choices, CategoryOptions, account, dto, canChooseAccount: IsAllAccounts);
        EditorOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the editor (Enter).</summary>
    [RelayCommand]
    public Task SaveAsync() => SaveCoreAsync(andNew: false);

    /// <summary>Saves and starts another new transaction (Ctrl/Cmd+Enter).</summary>
    [RelayCommand]
    public Task SaveAndNewAsync() => SaveCoreAsync(andNew: true);

    /// <summary>Closes the editor without saving (Esc).</summary>
    [RelayCommand]
    public void CancelEdit()
    {
        if (Editor is null)
        {
            return;
        }

        Editor = null;
        EditorClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toggles cleared on the selection (C).</summary>
    [RelayCommand]
    public async Task ToggleClearedAsync()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (_selection.Count == 1)
            {
                await _transactions.ToggleClearedAsync(_selection[0].Id, CancellationToken.None);
            }
            else
            {
                var clear = _selection.Any(r => r.IsUncleared);
                await _transactions.SetClearedAsync(Ids(), clear, CancellationToken.None);
            }
        });
    }

    /// <summary>Toggles cleared on one row (the cleared-column button).</summary>
    [RelayCommand]
    public Task ToggleRowClearedAsync(RegisterRowViewModel? row) => row is not { IsLoaded: true } ? Task.CompletedTask
        : RunAsync(() => _transactions.ToggleClearedAsync(row.Id, CancellationToken.None));

    /// <summary>Approves the selection (A).</summary>
    [RelayCommand]
    public Task ApproveAsync() => _selection.Count == 0 ? Task.CompletedTask
        : RunAsync(() => _transactions.ApproveAsync(Ids(), CancellationToken.None));

    /// <summary>Soft-deletes the selection with an undo toast (Delete).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public Task DeleteSelectedAsync() => RunAsync(async () =>
    {
        var count = await _transactions.DeleteAsync(Ids(), CancellationToken.None);
        _status.Show(LedgerText.Format(Strings.Status_Deleted, count), offerUndo: true);
    });

    /// <summary>Marks the selection cleared.</summary>
    [RelayCommand]
    public Task MarkClearedAsync() => RunAsync(() => _transactions.SetClearedAsync(Ids(), true, CancellationToken.None));

    /// <summary>Marks the selection uncleared.</summary>
    [RelayCommand]
    public Task MarkUnclearedAsync() => RunAsync(() => _transactions.SetClearedAsync(Ids(), false, CancellationToken.None));

    /// <summary>Categorizes the selection through a picker.</summary>
    [RelayCommand]
    public async Task CategorizeSelectedAsync()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var picker = new PickerDialogViewModel(Strings.Bulk_CategorizeTitle, LedgerText.Format(Strings.Bulk_CategorizePrompt, _selection.Count),
            CategoryOptions.Select(c => new PickerItem(c.Id!.Value, c.FullName)).ToList());
        if (await _dialogs.ShowAsync(picker) && picker.Selected is { } choice)
        {
            await RunAsync(async () =>
            {
                var changed = await _transactions.CategorizeAsync(Ids(), choice.Id, CancellationToken.None);
                _status.Show(LedgerText.Format(Strings.Status_Categorized, changed, choice.Label), offerUndo: changed > 0);
            });
        }
    }

    /// <summary>Moves the selection to another account through a picker.</summary>
    [RelayCommand]
    public async Task MoveSelectedAsync()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var picker = new PickerDialogViewModel(Strings.Bulk_MoveTitle, LedgerText.Format(Strings.Bulk_MovePrompt, _selection.Count),
            AccountOptions.Where(a => !a.IsClosed && a.Id != AccountId).Select(a => new PickerItem(a.Id, a.Name)).ToList());
        if (await _dialogs.ShowAsync(picker) && picker.Selected is { } choice)
        {
            await RunAsync(async () =>
            {
                var moved = await _transactions.MoveToAccountAsync(Ids(), choice.Id, CancellationToken.None);
                _status.Show(LedgerText.Format(Strings.Status_Moved, moved, choice.Label), offerUndo: moved > 0);
            });
        }
    }

    /// <summary>Opens the reconcile bar.</summary>
    [RelayCommand]
    public void StartReconcile()
    {
        if (Account is not { IsClosed: false } account || Summary is null)
        {
            return;
        }

        Reconcile = new ReconcileViewModel(_transactions, account.Id, account.Balance.Currency, Summary.Cleared, FinishedReconcileAsync, () => Reconcile = null);
    }

    /// <summary>
    /// Imports a bank file into this register's account, or into a chosen account from All
    /// Accounts (F-TXN-2). The register refreshes through <see cref="LedgerChanged"/>.
    /// </summary>
    [RelayCommand]
    public Task ImportFileAsync() => CanImportFile ? _import.ImportAsync(AccountId) : Task.CompletedTask;

    /// <summary>Opens the edit-account dialog.</summary>
    [RelayCommand]
    public async Task EditAccountAsync()
    {
        if (Account is null)
        {
            return;
        }

        var dialog = new AccountEditorViewModel(_accounts, Account);
        if (await _dialogs.ShowAsync(dialog))
        {
            _status.Show(LedgerText.Format(Strings.Status_AccountSaved, dialog.Result?.Name ?? Account.Name), offerUndo: true);
        }
    }

    /// <summary>Opens the record-balance dialog (tracking accounts).</summary>
    [RelayCommand]
    public async Task RecordBalanceAsync()
    {
        if (Account is null)
        {
            return;
        }

        var history = await _snapshots.GetSnapshotsAsync(Account.Id, CancellationToken.None);
        var dialog = new RecordBalanceViewModel(_snapshots, Account, history);
        if (await _dialogs.ShowAsync(dialog))
        {
            _status.Show(Strings.Status_BalanceRecorded, offerUndo: true);
        }
    }

    /// <summary>Clears every filter.</summary>
    [RelayCommand]
    public void ClearFilters()
    {
        _suppressFilterReload = true;
        SearchText = null;
        SelectedDatePreset = DatePresets[0];
        SelectedStatusFilter = StatusFilters[0];
        SelectedCategoryFilter = CategoryOption.All;
        UnapprovedOnly = false;
        _suppressFilterReload = false;
        _ = ReloadRowsAsync();
    }

    /// <summary>Retries after a load error.</summary>
    [RelayCommand]
    public void Retry() => Loading = LoadAsync(showSpinner: true);

    /// <summary>Toggles the split lines of a row.</summary>
    [RelayCommand]
    public void ToggleExpanded(RegisterRowViewModel? row)
    {
        if (row is { IsSplit: true })
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    /// <summary>The filter the grid is showing.</summary>
    public RegisterFilter CurrentFilter()
    {
        var (from, to) = DateRange(SelectedDatePreset?.Value ?? DatePreset.AllDates, DateOnly.FromDateTime(DateTime.Today));
        IReadOnlyCollection<TransactionStatus>? statuses = (SelectedStatusFilter?.Value ?? StatusFilter.All) switch
        {
            StatusFilter.Uncleared => [TransactionStatus.Uncleared],
            StatusFilter.Cleared => [TransactionStatus.Cleared],
            StatusFilter.Reconciled => [TransactionStatus.Reconciled],
            StatusFilter.NotReconciled => [TransactionStatus.Uncleared, TransactionStatus.Cleared],
            _ => null,
        };
        return new RegisterFilter(AccountId, from, to, statuses, SelectedCategoryFilter?.Id, UnapprovedOnly, SearchText);
    }

    /// <summary>First and last day of a preset relative to <paramref name="today"/>.</summary>
    public static (DateOnly? From, DateOnly? To) DateRange(DatePreset preset, DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        return preset switch
        {
            DatePreset.ThisMonth => (month, month.AddMonths(1).AddDays(-1)),
            DatePreset.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            DatePreset.LastThreeMonths => (month.AddMonths(-2), month.AddMonths(1).AddDays(-1)),
            DatePreset.ThisYear => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            DatePreset.LastYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            _ => (null, null),
        };
    }

    partial void OnSearchTextChanged(string? value) => FilterChanged();

    partial void OnSelectedDatePresetChanged(Choice<DatePreset> value) => FilterChanged();

    partial void OnSelectedStatusFilterChanged(Choice<StatusFilter> value) => FilterChanged();

    partial void OnSelectedCategoryFilterChanged(CategoryOption value) => FilterChanged();

    partial void OnUnapprovedOnlyChanged(bool value) => FilterChanged();

    private static string Label(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;

    private IReadOnlyCollection<Guid> Ids() => _selection.Select(r => r.Id).ToList();

    private void FilterChanged()
    {
        if (!_suppressFilterReload)
        {
            _ = ReloadRowsAsync();
        }
    }

    private async Task FinishedReconcileAsync(ReconciliationResult result)
    {
        Reconcile = null;
        _status.Show(result.AdjustmentTransactionId is null
            ? LedgerText.Format(Strings.Status_Reconciled, result.LockedCount)
            : LedgerText.Format(Strings.Status_ReconciledWithAdjustment, result.LockedCount, LedgerText.Money(result.Adjustment, Account?.Balance.Currency ?? Currency.Default)), offerUndo: true);
        await Loading;
    }

    private async Task SaveCoreAsync(bool andNew)
    {
        if (Editor is not { } editor || editor.BuildRequest() is not { } request)
        {
            return;
        }

        try
        {
            var saved = await _transactions.SaveAsync(request, CancellationToken.None);
            _selectAfterRefresh = saved.Id;
            _status.Show(editor.IsNew ? Strings.Status_Added : Strings.Status_Saved, offerUndo: true);
            if (andNew)
            {
                var account = editor.Account;
                Editor = new TransactionEditorViewModel(_payees, AccountOptions.Where(a => !a.IsClosed).ToList(), CategoryOptions, account, null, IsAllAccounts);
                if (editor.Date is { } date)
                {
                    Editor.Date = date;
                }

                EditorOpened?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Editor = null;
                EditorClosed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (LedgerValidationException ex)
        {
            editor.Error = LedgerText.Error(ex.Error);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    private Task ReloadRowsAsync()
    {
        Loading = LoadAsync(showSpinner: false, rowsOnly: true);
        return Loading;
    }

    // Loads header, pickers, count and first rows. A refresh with an unchanged count updates the
    // cached pages in place so selection and scroll position survive (incremental refresh).
    private async Task LoadAsync(bool showSpinner, bool rowsOnly = false)
    {
        var version = ++_loadVersion;
        if (showSpinner)
        {
            IsLoading = true;
        }

        try
        {
            ErrorMessage = null;
            if (!rowsOnly)
            {
                var accountsTask = _accounts.GetAccountsAsync(includeClosed: true, CancellationToken.None);
                var categoriesTask = _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
                var summaryTask = _register.GetSummaryAsync(AccountId, CancellationToken.None);
                var accounts = await accountsTask;
                var categories = await categoriesTask;
                var summary = await summaryTask;
                if (version != _loadVersion)
                {
                    return;
                }

                AccountOptions = accounts.Select(AccountOption.From).ToList();
                CategoryOptions = categories.Where(c => !c.IsCreditCardPayment).Select(CategoryOption.From).ToList();
                var filters = new List<CategoryOption> { CategoryOption.All };
                filters.AddRange(CategoryOptions);
                if (CategoryFilters.Count != filters.Count || !CategoryFilters.SequenceEqual(filters))
                {
                    var selected = SelectedCategoryFilter;
                    _suppressFilterReload = true;
                    CategoryFilters = filters;
                    SelectedCategoryFilter = filters.FirstOrDefault(f => f.Id == selected?.Id) ?? CategoryOption.All;
                    _suppressFilterReload = false;
                }

                Account = AccountId is { } id ? accounts.FirstOrDefault(a => a.Id == id) : null;
                HasNoAccounts = accounts.Count == 0;
                Summary = summary;
                SnapshotText = null;
                if (Account is { IsOnBudget: false } tracking)
                {
                    var latest = (await _snapshots.GetSnapshotsAsync(tracking.Id, CancellationToken.None)).FirstOrDefault();
                    SnapshotText = latest is null ? null : LedgerText.Format(Strings.Register_SnapshotValue,
                        LedgerText.Money(latest.Balance, tracking.Balance.Currency), latest.Date.ToString("d", CultureInfo.CurrentCulture));
                }

                OnPropertyChanged(nameof(CanAddTransaction));
            }

            var count = await _register.CountAsync(CurrentFilter(), CancellationToken.None);
            if (version != _loadVersion)
            {
                return;
            }

            if (count == Rows.Count && !rowsOnly && Rows.Count > 0)
            {
                await Rows.RefreshLoadedAsync();
            }
            else
            {
                Rows.Reset(count);
                SetSelection([]);
            }

            RowCount = count;
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyMessage));
            if (Reconcile is { } reconcile)
            {
                await reconcile.RefreshAsync();
            }

            if (_selectAfterRefresh is { } target)
            {
                _selectAfterRefresh = null;
                var index = await _register.IndexOfAsync(CurrentFilter(), _sort, target, CancellationToken.None);
                if (index >= 0 && version == _loadVersion)
                {
                    await Rows.GetLoadedAsync(index);
                    SelectIndexRequested?.Invoke(this, index);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _loadVersion)
            {
                ErrorMessage = LedgerText.Format(Strings.Register_ErrorLoading, ex.Message);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }
}
