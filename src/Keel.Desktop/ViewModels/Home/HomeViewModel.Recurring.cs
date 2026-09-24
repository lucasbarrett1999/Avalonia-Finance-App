using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Forecast;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Bills;
using Keel.Desktop.ViewModels.Home;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The two M5 cards of the dashboard (PRD 9.2): upcoming bills in the next 7 days (recurring items and
/// scheduled transactions) and the forecast low point with a sparkline of the lowest-balance account.
/// </summary>
public sealed partial class HomeViewModel
{
    /// <summary>Days covered by the upcoming bills card.</summary>
    public const int UpcomingDays = 7;

    /// <summary>Rows shown on the upcoming bills card.</summary>
    public const int UpcomingRows = 6;

    private readonly IRecurringService? _recurring;
    private readonly IScheduledTransactionService? _scheduled;
    private readonly IForecastService? _forecast;

    /// <summary>Bills due in the next 7 days (and overdue scheduled ones), earliest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpcoming))]
    public partial IReadOnlyList<HomeBillRow> UpcomingBills { get; private set; } = [];

    /// <summary>Whether anything is due.</summary>
    public bool HasUpcoming => UpcomingBills.Count > 0;

    /// <summary>"4 bills, $1,234.56 in the next 7 days" or the empty-state line.</summary>
    [ObservableProperty]
    public partial string UpcomingSummary { get; private set; } = string.Empty;

    /// <summary>Detection has not run yet and nothing is scheduled (the card suggests running it).</summary>
    [ObservableProperty]
    public partial bool UpcomingNeedsDetection { get; private set; }

    /// <summary>Balances of the lowest-balance account over the forecast (major units, sparkline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForecast))]
    public partial IReadOnlyList<double> ForecastValues { get; private set; } = [];

    /// <summary>Whether the forecast card has a line.</summary>
    public bool HasForecast => ForecastValues.Count > 1;

    /// <summary>"$412.30".</summary>
    [ObservableProperty]
    public partial string ForecastLowText { get; private set; } = string.Empty;

    /// <summary>Whether the low point is below zero.</summary>
    [ObservableProperty]
    public partial bool IsForecastLowNegative { get; private set; }

    /// <summary>"Lowest on Oct 3 in Everyday Checking".</summary>
    [ObservableProperty]
    public partial string ForecastLowDetail { get; private set; } = string.Empty;

    /// <summary>"2 days below your $500.00 floor", "Stays above your floor" or "No floor set".</summary>
    [ObservableProperty]
    public partial string ForecastFloorText { get; private set; } = string.Empty;

    /// <summary>Whether any day dips below the floor (warning icon).</summary>
    [ObservableProperty]
    public partial bool IsBelowFloor { get; private set; }

    /// <summary>The forecast card's empty line (no cash account).</summary>
    [ObservableProperty]
    public partial string ForecastEmptyText { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Receive(RecurringChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Opens Bills.</summary>
    [RelayCommand]
    public void OpenBills() => _navigation.NavigateTo<BillsViewModel>();

    /// <summary>Opens the forecast report.</summary>
    [RelayCommand]
    public void OpenForecast() => _navigation.NavigateTo<ReportsViewModel>(ReportKind.Forecast);

    /// <summary>Opens a bill: its item in Bills, or the register of a scheduled transaction's account.</summary>
    [RelayCommand]
    public void OpenBill(HomeBillRow? row)
    {
        if (row?.ItemId is { } item)
        {
            _navigation.NavigateTo<BillsViewModel>(item);
        }
        else if (row?.AccountId is { } account)
        {
            _navigation.NavigateTo<AccountsViewModel>(account);
        }
    }

    private async Task LoadRecurringCardsAsync(DateOnly today, int version)
    {
        if (_recurring is null || _scheduled is null || _forecast is null)
        {
            return;
        }

        var end = today.AddDays(UpcomingDays - 1);
        var items = await _recurring.GetItemsAsync(new RecurringItemFilter([RecurringStatus.Active, RecurringStatus.Detected]), CancellationToken.None);
        var occurrences = await _recurring.GetOccurrencesAsync(today, end, CancellationToken.None);
        var scheduled = await _scheduled.GetUpcomingAsync(today.AddDays(-14), end, null, CancellationToken.None);
        var lastRun = await _recurring.GetLastDetectionDateAsync(CancellationToken.None);
        var settings = await _forecast.GetSettingsAsync(CancellationToken.None);
        var forecast = await _forecast.GetForecastAsync(new ForecastRequest(90, settings.IncludeDiscretionarySpend, settings.Floor is { } f ? new Money(f, ReadyToAssign.Currency ?? Currency.Default) : null), CancellationToken.None);
        if (version != _version)
        {
            return;
        }

        var byId = items.ToDictionary(i => i.Id);
        var rows = occurrences
            .Where(o => o.IsExpected && o.Amount.Amount < 0 && byId.ContainsKey(o.ItemId) && byId[o.ItemId].ScheduledTransactionId is null)
            .Select(o => new HomeBillRow(o.Date, byId[o.ItemId].PayeeName, -o.Amount.Amount, o.Amount.Currency, o.ItemId, byId[o.ItemId].AccountId, today, byId[o.ItemId].Status == RecurringStatus.Detected, OpenBillCommand))
            .Concat(scheduled
                .Where(s => s.Amount.Amount < 0 && s.TransferAccountName is null)
                .Select(s => new HomeBillRow(s.Date, s.PayeeName, -s.Amount.Amount, s.Amount.Currency,
                    items.FirstOrDefault(i => i.ScheduledTransactionId == s.ScheduledId)?.Id, s.AccountId, today, false, OpenBillCommand)))
            .OrderBy(r => r.Date).ThenByDescending(r => r.Amount)
            .ToList();
        UpcomingBills = rows.Take(UpcomingRows).ToList();
        UpcomingNeedsDetection = lastRun is null && items.Count == 0 && scheduled.Count == 0;
        var currency = rows.FirstOrDefault()?.Currency ?? forecast.Currency;
        UpcomingSummary = UpcomingNeedsDetection ? Strings.Home_UpcomingNeedsDetection
            : rows.Count == 0 ? Strings.Home_UpcomingNone
            : LedgerText.Format(rows.Count == 1 ? Strings.Home_UpcomingOne : Strings.Home_UpcomingMany, rows.Count, LedgerText.Money(rows.Sum(r => r.Amount), currency));

        var lowest = forecast.Accounts.OrderBy(a => a.LowestBalance.Amount).ThenBy(a => a.LowestDate).FirstOrDefault();
        if (lowest is null)
        {
            ForecastValues = [];
            ForecastLowText = ForecastLowDetail = ForecastFloorText = string.Empty;
            IsBelowFloor = false;
            ForecastEmptyText = Strings.Home_ForecastNoAccounts;
            return;
        }

        ForecastEmptyText = string.Empty;
        ForecastValues = lowest.Points.Select(p => ReportFormat.Major(p.Balance.Amount, p.Balance.Currency)).ToList();
        ForecastLowText = LedgerText.Money(lowest.LowestBalance.Amount, lowest.LowestBalance.Currency);
        IsForecastLowNegative = lowest.LowestBalance.Amount < 0;
        ForecastLowDetail = LedgerText.Format(Strings.Home_ForecastLowDetail, BillsFormat.ShortDate(lowest.LowestDate), lowest.Name);
        var belowDays = forecast.Accounts.SelectMany(a => a.BelowFloor).Select(p => p.Date).Distinct().Count();
        IsBelowFloor = belowDays > 0;
        ForecastFloorText = settings.Floor is not { } floor ? Strings.Home_ForecastNoFloor
            : belowDays == 0 ? LedgerText.Format(Strings.Home_ForecastAboveFloor, LedgerText.Money(floor, forecast.Currency))
            : LedgerText.Format(Strings.Home_ForecastBelowFloor, belowDays, LedgerText.Money(floor, forecast.Currency));
    }
}
