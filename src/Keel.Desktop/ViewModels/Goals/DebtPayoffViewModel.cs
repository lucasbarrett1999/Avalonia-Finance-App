using System.Data.Common;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Application.Debt;
using Keel.Application.Ledger;
using Keel.Application.Navigation;
using Keel.Desktop.Controls;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain;
using Keel.Domain.Debt;

namespace Keel.Desktop.ViewModels.Goals;

/// <summary>A debt row of the payoff table (and its chart band and legend swatch).</summary>
public sealed class DebtRowViewModel
{
    /// <summary>Creates the row.</summary>
    public DebtRowViewModel(DebtAccountDto debt, DebtSchedule plan, DebtSchedule minimumOnly, int slot, DebtPayoffOverview overview, IRelayCommand<DebtRowViewModel> open)
    {
        ArgumentNullException.ThrowIfNull(debt);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(minimumOnly);
        ArgumentNullException.ThrowIfNull(overview);
        var currency = overview.Currency;
        AccountId = debt.AccountId;
        Name = debt.Name;
        Slot = slot;
        Balances = plan.Balances;
        OwedText = ReportFormat.Money(debt.Owed, currency);
        RateText = LedgerText.Format(Strings.Debt_RateValue, AccountEditorViewModel.FormatRate(debt.InterestRateBps ?? 0));
        MinimumText = ReportFormat.Money(debt.MinimumPayment ?? 0, currency);
        PaymentText = ReportFormat.Money(plan.FirstPayment, currency);
        IsNever = !plan.PaysOff;
        PayoffText = plan.PayoffMonths is { } months ? DebtPayoffViewModel.MonthText(overview.MonthOf(months)) : Strings.Debt_Never;
        InterestText = plan.PaysOff ? ReportFormat.Money(plan.TotalInterest, currency) : Strings.Health_NoValue;
        AtMinimumText = minimumOnly.PayoffMonths is { } minMonths
            ? LedgerText.Format(Strings.Debt_AtMinimum, DebtPayoffViewModel.MonthText(overview.MonthOf(minMonths)), ReportFormat.Money(minimumOnly.TotalInterest, currency))
            : Strings.Debt_AtMinimumNever;
        CategoryText = debt.PaymentCategoryName is { } category
            ? debt.CurrentTarget is { } target
                ? LedgerText.Format(Strings.Debt_PaymentTarget, category, ReportFormat.Money(target, currency))
                : LedgerText.Format(Strings.Debt_PaymentCategory, category)
            : Strings.Debt_NoPaymentCategory;
        AutomationName = LedgerText.Format(Strings.Debt_RowAutomation, Name, OwedText, RateText, PayoffText);
        Open = open;
    }

    /// <summary>Account.</summary>
    public Guid AccountId { get; }

    /// <summary>Account name.</summary>
    public string Name { get; }

    /// <summary>Chart palette slot.</summary>
    public int Slot { get; }

    /// <summary>Owed at the start and after each month of the plan.</summary>
    public IReadOnlyList<long> Balances { get; }

    /// <summary>Owed now.</summary>
    public string OwedText { get; }

    /// <summary>"19.99%".</summary>
    public string RateText { get; }

    /// <summary>Minimum payment.</summary>
    public string MinimumText { get; }

    /// <summary>This month's planned payment (the target the plan sets).</summary>
    public string PaymentText { get; }

    /// <summary>"Aug 2029" or "Never".</summary>
    public string PayoffText { get; }

    /// <summary>Never paid off under this plan (warning icon and words).</summary>
    public bool IsNever { get; }

    /// <summary>Interest until payoff.</summary>
    public string InterestText { get; }

    /// <summary>"At the minimum alone: paid off Mar 2031 with $1,234.56 interest" (F-GOAL-2 "at current payment").</summary>
    public string AtMinimumText { get; }

    /// <summary>"Budgeted in Visa · target $150.00".</summary>
    public string CategoryText { get; }

    /// <summary>Screen-reader text of the row.</summary>
    public string AutomationName { get; }

    /// <summary>Opens the account's register.</summary>
    public IRelayCommand<DebtRowViewModel> Open { get; }
}

/// <summary>A debt left out of the plan until its rate and minimum payment are entered.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="Name">Account name.</param>
/// <param name="OwedText">Owed now.</param>
/// <param name="MissingText">What is missing.</param>
public sealed record DebtMissingRow(Guid AccountId, string Name, string OwedText, string MissingText)
{
    /// <summary>"Edit Store card".</summary>
    public string EditName => LedgerText.Format(Strings.Debt_EditAccountName, Name);

    /// <summary>Opens the account editor.</summary>
    public IAsyncRelayCommand<DebtMissingRow>? Edit { get; init; }
}

/// <summary>
/// The Debt payoff tab of Goals (F-GOAL-2, PRD 9.9, ADR 0093): every debt with its balance, rate, minimum,
/// payoff month and interest under the plan (and at the minimum alone), controls for the extra per month and
/// the ordering, a chart of balances over time, and "Set payment targets", which writes the plan's payments
/// as debt-payment targets through the budget (one undoable action).
/// </summary>
public sealed partial class DebtPayoffViewModel : ViewModelBase
{
    private readonly IDebtPayoffService _debts;
    private readonly IAccountService _accounts;
    private readonly INavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private int _version;

    /// <summary>Creates the tab.</summary>
    public DebtPayoffViewModel(IDebtPayoffService debts, IAccountService accounts, INavigationService navigation, DialogService dialogs, StatusService status)
    {
        _debts = debts;
        _accounts = accounts;
        _navigation = navigation;
        _dialogs = dialogs;
        _status = status;
        Orderings = [new(DebtOrdering.Avalanche, Strings.Debt_Avalanche), new(DebtOrdering.Snowball, Strings.Debt_Snowball)];
        SelectedOrdering = Orderings[0];
    }

    /// <summary>Raised when the chart data changed.</summary>
    public event EventHandler? ChartChanged;

    /// <summary>The ordering choices.</summary>
    public IReadOnlyList<Choice<DebtOrdering>> Orderings { get; }

    /// <summary>Snowball or avalanche.</summary>
    [ObservableProperty]
    public partial Choice<DebtOrdering> SelectedOrdering { get; set; }

    /// <summary>Extra paid every month on top of the minimums (minor units).</summary>
    [ObservableProperty]
    public partial long ExtraPerMonth { get; set; }

    /// <summary>The loaded plan.</summary>
    public DebtPayoffOverview? Overview { get; private set; }

    /// <summary>Budget currency (amount boxes).</summary>
    [ObservableProperty]
    public partial string Currency { get; private set; } = Keel.Domain.Currency.Default;

    /// <summary>Whether a load finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowEmptyState), nameof(ShowContent))]
    public partial bool IsInitialized { get; private set; }

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowLoading))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The loading state.</summary>
    public bool ShowLoading => !IsInitialized && !HasError;

    /// <summary>Whether any liability account is owed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowContent))]
    public partial bool HasDebts { get; private set; }

    /// <summary>The designed empty state (nothing owed).</summary>
    public bool ShowEmptyState => IsInitialized && !HasDebts;

    /// <summary>The planner.</summary>
    public bool ShowContent => IsInitialized && HasDebts;

    /// <summary>Planned debts (with a rate and a minimum), in priority order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyCanExecuteChangedFor(nameof(SetTargetsCommand))]
    public partial IReadOnlyList<DebtRowViewModel> Rows { get; private set; } = [];

    /// <summary>Whether any debt can be planned.</summary>
    public bool HasPlan => Rows.Count > 0;

    /// <summary>Debts without a rate or a minimum.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissing))]
    public partial IReadOnlyList<DebtMissingRow> Missing { get; private set; } = [];

    /// <summary>Whether some debts need details.</summary>
    public bool HasMissing => Missing.Count > 0;

    /// <summary>"Debt-free by Aug 2029" or the never line.</summary>
    [ObservableProperty]
    public partial string HeadlineText { get; private set; } = string.Empty;

    /// <summary>Some debt is never paid off under this plan.</summary>
    [ObservableProperty]
    public partial bool IsNever { get; private set; }

    /// <summary>"$605.00 a month · $2,950.12 interest in total".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>Comparison with paying only the minimums.</summary>
    [ObservableProperty]
    public partial string ComparisonText { get; private set; } = string.Empty;

    /// <summary>First month of the chart.</summary>
    public DateOnly StartMonth => Overview?.StartMonth ?? default;

    /// <summary>Total owed after each month when paying only the minimums (chart line).</summary>
    public IReadOnlyList<long> MinimumOnlyTotals { get; private set; } = [];

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The account editor opened from the "needs details" list (tests drive it).</summary>
    public AccountEditorViewModel? Editor { get; private set; }

    /// <summary>"Aug 2029".</summary>
    public static string MonthText(DateOnly month) => month.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"19 months", "1 month".</summary>
    public static string MonthsText(int months) => months == 1 ? Strings.Debt_OneMonth : LedgerText.Format(Strings.Debt_Months, months);

    /// <summary>Loads once (the tab was shown).</summary>
    public void EnsureLoaded()
    {
        if (!IsInitialized && Loading.IsCompleted)
        {
            Loading = LoadAsync();
        }
    }

    /// <summary>Reloads after a ledger or budget change, when already shown once.</summary>
    public void Refresh()
    {
        if (IsInitialized)
        {
            Loading = LoadAsync();
        }
    }

    /// <summary>Sets the plan's payments as debt-payment targets (one undoable budget action).</summary>
    [RelayCommand(CanExecute = nameof(HasPlan))]
    public async Task SetTargetsAsync()
    {
        try
        {
            var result = await _debts.SetPaymentTargetsAsync(ExtraPerMonth, SelectedOrdering.Value,
                new NewPaymentCategory(Strings.Debt_PaymentGroup, Strings.Debt_PaymentCategoryFormat), CancellationToken.None);
            var message = result switch
            {
                { TargetsSet: 0 } => Strings.Debt_TargetsNone,
                { CategoriesCreated: > 0 } => LedgerText.Format(Strings.Debt_TargetsSetCreated, result.TargetsSet, result.CategoriesCreated),
                { TargetsSet: 1 } => Strings.Debt_TargetsSetOne,
                _ => LedgerText.Format(Strings.Debt_TargetsSet, result.TargetsSet),
            };
            _status.Show(message, offerUndo: result.TargetsSet > 0);
            Loading = LoadAsync();
            await Loading;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DbException or LedgerValidationException)
        {
            _status.Show(LedgerText.Format(Strings.Debt_TargetsFailed, ex.Message), isError: true);
        }
    }

    /// <summary>Opens a debt's register.</summary>
    [RelayCommand]
    public void OpenDebt(DebtRowViewModel? row)
    {
        if (row is not null)
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(row.AccountId, null, CategoryOption.All));
        }
    }

    /// <summary>Chart click on a band.</summary>
    public void OpenBand(int index)
    {
        if (index >= 0 && index < Rows.Count)
        {
            OpenDebt(Rows[index]);
        }
    }

    /// <summary>Edits a debt's account to add its rate and minimum payment.</summary>
    [RelayCommand]
    public async Task EditAccountAsync(DebtMissingRow? row)
    {
        if (row is null || await _accounts.GetAccountAsync(row.AccountId, CancellationToken.None) is not { } account)
        {
            return;
        }

        var editor = Editor = new AccountEditorViewModel(_accounts, account);
        try
        {
            if (await _dialogs.ShowAsync(editor))
            {
                _status.Show(LedgerText.Format(Strings.Status_AccountSaved, editor.Result?.Name ?? account.Name), offerUndo: true);
                Loading = LoadAsync();
            }
        }
        finally
        {
            Editor = null;
        }
    }

    /// <summary>Adds a loan or card from the empty state.</summary>
    [RelayCommand]
    public async Task AddAccountAsync()
    {
        var editor = Editor = new AccountEditorViewModel(_accounts);
        editor.SelectedType = editor.Types.First(t => t.Value == AccountType.Loan);
        try
        {
            if (await _dialogs.ShowAsync(editor) && editor.Result is { } created)
            {
                _status.Show(LedgerText.Format(Strings.Status_AccountAdded, created.Name), offerUndo: true);
                Loading = LoadAsync();
            }
        }
        finally
        {
            Editor = null;
        }
    }

    partial void OnSelectedOrderingChanged(Choice<DebtOrdering> value) => Refresh();

    partial void OnExtraPerMonthChanged(long value)
    {
        if (value < 0)
        {
            ExtraPerMonth = 0;
            return;
        }

        Refresh();
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var overview = await _debts.GetPlanAsync(ExtraPerMonth, SelectedOrdering.Value, CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            ErrorMessage = null;
            Apply(overview);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or ArgumentException)
        {
            if (version == _version)
            {
                ErrorMessage = LedgerText.Format(Strings.Debt_ErrorLoading, ex.Message);
            }
        }
        finally
        {
            if (version == _version)
            {
                IsInitialized = true;
            }
        }
    }

    private void Apply(DebtPayoffOverview overview)
    {
        Overview = overview;
        Currency = overview.Currency;
        HasDebts = overview.HasDebts;
        var plan = overview.Plan;
        var minimum = overview.MinimumOnly;
        var rows = new List<DebtRowViewModel>();
        for (var i = 0; i < plan.Debts.Count; i++)
        {
            var slot = i < ChartPalette.SlotCount ? i : ChartPalette.OtherSlot;
            rows.Add(new DebtRowViewModel(overview.Debts[i], plan.Debts[i], minimum.Debts[i], slot, overview, OpenDebtCommand));
        }

        Rows = rows;
        Missing = overview.MissingTerms.Select(d => new DebtMissingRow(
            d.AccountId,
            d.Name,
            ReportFormat.Money(d.Owed, overview.Currency),
            (d.InterestRateBps, d.MinimumPayment) switch
            {
                (null, null) => Strings.Debt_Missing_Both,
                (null, _) => Strings.Debt_Missing_Rate,
                _ => Strings.Debt_Missing_Minimum,
            })
        { Edit = EditAccountCommand }).ToList();

        IsNever = !plan.PaysOffAll;
        HeadlineText = plan.PayoffMonths is { } months ? LedgerText.Format(Strings.Debt_DebtFree, MonthText(overview.MonthOf(months))) : Strings.Debt_NeverFree;
        SummaryText = plan.PaysOffAll
            ? LedgerText.Format(Strings.Debt_PlanSummary, ReportFormat.Money(plan.MonthlyOutlay, overview.Currency), ReportFormat.Money(plan.TotalInterest, overview.Currency))
            : LedgerText.Format(Strings.Debt_PlanOutlay, ReportFormat.Money(plan.MonthlyOutlay, overview.Currency));
        ComparisonText = (plan.PayoffMonths, minimum.PayoffMonths) switch
        {
            (null, _) => Strings.Debt_ComparisonNeverPlan,
            ({ } p, { } m) when p < m || plan.TotalInterest < minimum.TotalInterest =>
                LedgerText.Format(Strings.Debt_Comparison, MonthsText(m - p), ReportFormat.Money(minimum.TotalInterest - plan.TotalInterest, overview.Currency)),
            ({ }, null) => Strings.Debt_ComparisonNeverMin,
            _ => Strings.Debt_ComparisonSame,
        };
        MinimumOnlyTotals = minimum.TotalBalances;
        OnPropertyChanged(nameof(StartMonth));
        ChartChanged?.Invoke(this, EventArgs.Empty);
    }
}
