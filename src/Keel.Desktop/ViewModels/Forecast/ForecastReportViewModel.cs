using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Forecast;
using Keel.Application.Reports;
using Keel.Desktop.Controls;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Bills;
using Keel.Domain;
using Keel.Domain.Forecast;

// Lives with the other reports so the ViewLocator maps it to Views/Reports/ForecastReportView.
namespace Keel.Desktop.ViewModels.Reports;

/// <summary>One line of the forecast chart and its legend row.</summary>
/// <param name="AccountId">Account, or null for all cash accounts combined.</param>
/// <param name="Name">Name.</param>
/// <param name="Slot">Palette slot (<see cref="ChartPalette.InkSlot"/> for the combined line).</param>
/// <param name="Balances">Closing balance per day, day 0 first.</param>
/// <param name="Lowest">Lowest balance.</param>
/// <param name="LowestDate">Its date.</param>
/// <param name="BelowFloorDays">Days below the floor.</param>
/// <param name="Currency">Currency.</param>
public sealed record ForecastSeriesItem(Guid? AccountId, string Name, int Slot, IReadOnlyList<long> Balances, long Lowest, DateOnly LowestDate, int BelowFloorDays, string Currency)
{
    /// <summary>The combined line.</summary>
    public bool IsCombined => AccountId is null;

    /// <summary>"$1,234.56 on Oct 3".</summary>
    public string LowestText => LedgerText.Format(Strings.Forecast_LowOn, LedgerText.Money(Lowest, Currency), BillsFormat.ShortDate(LowestDate));

    /// <summary>Lowest balance is below zero.</summary>
    public bool IsLowestNegative => Lowest < 0;

    /// <summary>Balance at the end of the horizon.</summary>
    public string EndText => Balances.Count == 0 ? string.Empty : LedgerText.Money(Balances[^1], Currency);
}

/// <summary>A run of consecutive days one account spends below the floor.</summary>
/// <param name="From">First day below.</param>
/// <param name="To">Last day below.</param>
/// <param name="AccountId">Account.</param>
/// <param name="AccountName">Account name.</param>
/// <param name="Date">The lowest day of the run.</param>
/// <param name="Balance">The lowest closing balance of the run.</param>
/// <param name="Currency">Currency.</param>
public sealed record ForecastFloorRow(DateOnly From, DateOnly To, Guid? AccountId, string AccountName, DateOnly Date, long Balance, string Currency)
{
    /// <summary>Days in the run.</summary>
    public int DayCount => To.DayNumber - From.DayNumber + 1;

    /// <summary>"Wed, Oct 1" or "Oct 3 – Dec 23 (82 days)".</summary>
    public string DateText => DayCount == 1
        ? BillsFormat.DayDate(From)
        : LedgerText.Format(Strings.Forecast_FloorRun, BillsFormat.ShortDate(From), BillsFormat.ShortDate(To), DayCount);

    /// <summary>Lowest balance of the run.</summary>
    public string BalanceText => LedgerText.Money(Balance, Currency);

    /// <summary>"lowest on Dec 3".</summary>
    public string LowestText => LedgerText.Format(Strings.Forecast_FloorRunLowest, BillsFormat.ShortDate(Date));

    /// <summary>Screen-reader text.</summary>
    public string AutomationName => LedgerText.Format(Strings.Forecast_FloorRowAutomation, DateText, AccountName, BalanceText);
}

/// <summary>One entry of an explained day.</summary>
/// <param name="Label">Payee or description.</param>
/// <param name="KindText">"Scheduled", "Recurring", …</param>
/// <param name="AmountText">Signed amount.</param>
/// <param name="Note">Overdue note, or empty.</param>
public sealed record ForecastExplainEntry(string Label, string KindText, string AmountText, string Note)
{
    /// <summary>Whether a note is shown.</summary>
    public bool HasNote => Note.Length > 0;
}

/// <summary>"Show the math" for one forecast day (principle 7).</summary>
/// <param name="Title">"Wed, Oct 1 · Everyday Checking".</param>
/// <param name="OpeningText">Opening balance.</param>
/// <param name="Entries">What lands on the day.</param>
/// <param name="ClosingText">Closing balance.</param>
public sealed record ForecastExplanation(string Title, string OpeningText, IReadOnlyList<ForecastExplainEntry> Entries, string ClosingText)
{
    /// <summary>Whether anything lands on the day.</summary>
    public bool HasEntries => Entries.Count > 0;
}

/// <summary>
/// The cash-flow forecast (F-REP-4) inside Reports: the next 90 days of closing balances per on-budget cash
/// account and combined, the lowest-balance marker, the user's floor and the days below it, the
/// average-discretionary-spend toggle, what was left out, and a per-day explanation on click. The floor
/// and toggle are data-file settings. Uses the toolbar's accounts filter; the date range does not apply.
/// </summary>
public sealed partial class ForecastReportViewModel : ReportViewModel
{
    private readonly IForecastService _forecast;
    private readonly TimeProvider _time;
    private bool _settingsLoaded;
    private bool _suppressSave;
    private int _belowDays;

    /// <summary>Creates the report.</summary>
    public ForecastReportViewModel(ReportsViewModel owner, IForecastService forecast, TimeProvider time)
        : base(owner, includeTransfers: true, includeTracking: false)
    {
        _forecast = forecast;
        _time = time;
    }

    /// <inheritdoc />
    public override ReportKind Kind => ReportKind.Forecast;

    /// <inheritdoc />
    public override string Title => Strings.Forecast_Title;

    /// <inheritdoc />
    public override string Description => Strings.Forecast_Description;

    /// <inheritdoc />
    public override string IconKey => "Icon.Report.Calendar";

    /// <inheritdoc />
    public override bool SupportsTransfers => false;

    /// <inheritdoc />
    public override bool SupportsRange => false;

    /// <inheritdoc />
    public override bool SupportsTracking => false;

    /// <inheritdoc />
    public override string? RangeTextOverride
    {
        get
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            return LedgerText.Format(Strings.Forecast_Horizon, ReportFormat.Range(today, today.AddDays(Days)));
        }
    }

    /// <inheritdoc />
    public override string CsvFileName => "cash-flow-forecast.csv";

    /// <summary>Days projected.</summary>
    public int Days => 90;

    /// <summary>The loaded forecast.</summary>
    public ForecastDto? Forecast { get; private set; }

    /// <summary>The combined line first, then one per account.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ForecastSeriesItem> Series { get; private set; } = [];

    /// <summary>Dates of the horizon, day 0 first.</summary>
    public IReadOnlyList<DateOnly> Dates { get; private set; } = [];

    /// <summary>Lowest combined balance.</summary>
    [ObservableProperty]
    public partial string LowestText { get; private set; } = string.Empty;

    /// <summary>Whether it is below zero.</summary>
    [ObservableProperty]
    public partial bool IsLowestNegative { get; private set; }

    /// <summary>"on Wed, Oct 1, in Everyday Checking".</summary>
    [ObservableProperty]
    public partial string LowestDetailText { get; private set; } = string.Empty;

    /// <summary>Combined balance today (cleared).</summary>
    [ObservableProperty]
    public partial string StartText { get; private set; } = string.Empty;

    /// <summary>Combined balance at the end of the horizon.</summary>
    [ObservableProperty]
    public partial string EndText { get; private set; } = string.Empty;

    /// <summary>Whether a floor is set.</summary>
    [ObservableProperty]
    public partial bool UseFloor { get; set; }

    /// <summary>The floor (minor units).</summary>
    [ObservableProperty]
    public partial long FloorAmount { get; set; }

    /// <summary>Subtract the average daily discretionary spend of the last 90 days.</summary>
    [ObservableProperty]
    public partial bool IncludeDiscretionary { get; set; }

    /// <summary>Budget currency.</summary>
    [ObservableProperty]
    public partial string Currency { get; private set; } = Keel.Domain.Currency.Default;

    /// <summary>Days below the floor (per account), earliest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFloorRows), nameof(FloorSummary))]
    public partial IReadOnlyList<ForecastFloorRow> FloorRows { get; private set; } = [];

    /// <summary>Whether any day is below the floor.</summary>
    public bool HasFloorRows => FloorRows.Count > 0;

    /// <summary>"4 days below $500.00" / "Stays above $500.00" / "Set a floor …".</summary>
    public string FloorSummary => !UseFloor ? Strings.Forecast_NoFloor
        : FloorRows.Count == 0 ? LedgerText.Format(Strings.Forecast_AboveFloor, LedgerText.Money(FloorAmount, Currency))
        : LedgerText.Format(Strings.Forecast_BelowFloorCount, _belowDays, LedgerText.Money(FloorAmount, Currency));

    /// <summary>How the discretionary spend was computed, per account.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> DiscretionaryLines { get; private set; } = [];

    /// <summary>Sources left out, with the reason.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipped))]
    public partial IReadOnlyList<string> SkippedLines { get; private set; } = [];

    /// <summary>Whether anything was left out.</summary>
    public bool HasSkipped => SkippedLines.Count > 0;

    /// <summary>The explained day.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExplanation))]
    public partial ForecastExplanation? Explanation { get; private set; }

    /// <summary>Whether a day is explained.</summary>
    public bool HasExplanation => Explanation is not null;

    /// <summary>The explanation in progress (tests await it).</summary>
    public Task Explaining { get; private set; } = Task.CompletedTask;

    /// <summary>Explains a day of the combined line or of an account.</summary>
    public void Explain(DateOnly date, Guid? accountId) => Explaining = ExplainAsync(date, accountId);

    /// <summary>Chart click: <paramref name="series"/> is the index in <see cref="Series"/>, <paramref name="index"/> the day.</summary>
    public void OpenPoint(int series, int index)
    {
        if (series >= 0 && series < Series.Count && index >= 0 && index < Dates.Count)
        {
            Explain(Dates[index], Series[series].AccountId);
        }
    }

    /// <summary>Explains a floor row's day.</summary>
    [RelayCommand]
    public void ExplainRow(ForecastFloorRow? row)
    {
        if (row is not null)
        {
            Explain(row.Date, row.AccountId);
        }
    }

    /// <summary>Explains the lowest day of a series (legend click).</summary>
    [RelayCommand]
    public void ExplainLowest(ForecastSeriesItem? item)
    {
        if (item is not null)
        {
            Explain(item.LowestDate, item.AccountId);
        }
    }

    /// <summary>Closes the explanation.</summary>
    [RelayCommand]
    public void CloseExplanation() => Explanation = null;

    /// <inheritdoc />
    public override string ToCsv()
    {
        var header = new List<string> { Strings.Reports_Csv_Date };
        header.AddRange(Series.Select(s => s.Name));
        var rows = new List<IReadOnlyList<string>> { header };
        for (var i = 0; i < Dates.Count; i++)
        {
            var line = new List<string> { Dates[i].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            line.AddRange(Series.Select(s => ReportFormat.CsvAmount(s.Balances[i], s.Currency)));
            rows.Add(line);
        }

        return ReportFormat.Csv(rows);
    }

    /// <inheritdoc />
    protected override async Task LoadCoreAsync(IReportService service, ReportQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_settingsLoaded)
        {
            var settings = await _forecast.GetSettingsAsync(ct);
            _suppressSave = true;
            UseFloor = settings.Floor is not null;
            FloorAmount = settings.Floor ?? 0;
            IncludeDiscretionary = settings.IncludeDiscretionarySpend;
            _suppressSave = false;
            _settingsLoaded = true;
        }

        var forecast = await _forecast.GetForecastAsync(Request(query), ct);
        Forecast = forecast;
        Currency = forecast.Currency;
        Dates = forecast.Combined.Points.Select(p => p.Date).ToList();
        var items = new List<ForecastSeriesItem> { Item(forecast.Combined, null, Strings.Forecast_Combined, ChartPalette.InkSlot) };
        for (var i = 0; i < forecast.Accounts.Count; i++)
        {
            var a = forecast.Accounts[i];
            items.Add(Item(a, a.AccountId, a.Name, i < ChartPalette.SlotCount ? i : ChartPalette.OtherSlot));
        }

        Series = items;
        HasData = forecast.Accounts.Count > 0;
        var combined = items[0];
        var lowestAccount = items.Skip(1).OrderBy(s => s.Lowest).ThenBy(s => s.LowestDate).FirstOrDefault();
        LowestText = LedgerText.Money(combined.Lowest, Currency);
        IsLowestNegative = combined.Lowest < 0;
        LowestDetailText = lowestAccount is null
            ? string.Empty
            : LedgerText.Format(Strings.Forecast_LowestDetail, BillsFormat.DayDate(combined.LowestDate), lowestAccount.Name, lowestAccount.LowestText);
        StartText = combined.Balances.Count > 0 ? LedgerText.Money(combined.Balances[0], Currency) : string.Empty;
        EndText = combined.EndText;
        _belowDays = forecast.Accounts.SelectMany(a => a.BelowFloor).Select(p => p.Date).Distinct().Count();
        FloorRows = forecast.Accounts
            .SelectMany(a => Runs(a, Currency))
            .OrderBy(r => r.From).ThenBy(r => r.AccountName, StringComparer.CurrentCulture)
            .ToList();
        OnPropertyChanged(nameof(FloorSummary));
        var names = forecast.Accounts.ToDictionary(a => a.AccountId ?? Guid.Empty, a => a.Name);
        DiscretionaryLines = forecast.Discretionary
            .Select(d => LedgerText.Format(Strings.Forecast_DiscretionaryLine, names.GetValueOrDefault(d.AccountId, string.Empty),
                LedgerText.Money(d.Daily, Currency), LedgerText.Money(d.TotalSpend, Currency), d.Days, d.TransactionCount, d.ExcludedCount))
            .ToList();
        SkippedLines = forecast.Skipped
            .Select(s => LedgerText.Format(Strings.Forecast_SkippedLine, s.Label ?? Strings.Alerts_UnknownPayee,
                Strings.ResourceManager.GetString("ForecastSkip_" + s.Reason, Strings.Culture) ?? s.Reason.ToString()))
            .Distinct()
            .ToList();
        if (Explanation is not null)
        {
            Explanation = null;
        }
    }

    partial void OnUseFloorChanged(bool value) => SettingsChanged();

    partial void OnFloorAmountChanged(long value)
    {
        if (UseFloor)
        {
            SettingsChanged();
        }
    }

    partial void OnIncludeDiscretionaryChanged(bool value) => SettingsChanged();

    private ForecastSeriesItem Item(ForecastSeriesDto s, Guid? accountId, string name, int slot) =>
        new(accountId, name, slot, s.Points.Select(p => p.Balance.Amount).ToList(), s.LowestBalance.Amount, s.LowestDate, s.BelowFloor.Count, Currency);

    private ForecastRequest Request(ReportQuery? query) => new(
        Days,
        IncludeDiscretionary,
        UseFloor ? new Money(FloorAmount, Currency) : null,
        query?.AccountIds);

    private void SettingsChanged()
    {
        OnPropertyChanged(nameof(FloorSummary));
        if (_suppressSave || !_settingsLoaded)
        {
            return;
        }

        _ = SaveAndReloadAsync();
    }

    private async Task SaveAndReloadAsync()
    {
        await _forecast.SaveSettingsAsync(new ForecastSettings(UseFloor ? FloorAmount : null, IncludeDiscretionary), CancellationToken.None);
        Owner.OptionsChanged(this);
    }

    private async Task ExplainAsync(DateOnly date, Guid? accountId)
    {
        var explanation = await _forecast.ExplainDayAsync(Request(Query), date, accountId, CancellationToken.None);
        var account = accountId is { } id ? Series.FirstOrDefault(s => s.AccountId == id)?.Name ?? string.Empty : Strings.Forecast_Combined;
        Explanation = new ForecastExplanation(
            BillsFormat.DayDate(date) + " · " + account,
            LedgerText.Money(explanation.Opening.Amount, explanation.Opening.Currency),
            explanation.Entries.Select(e => new ForecastExplainEntry(
                e.Label.Length == 0 ? KindText(e.Kind) : e.Label,
                KindText(e.Kind),
                (e.Amount.Amount > 0 ? "+" : string.Empty) + LedgerText.Money(e.Amount.Amount, e.Amount.Currency),
                e.IsOverdue ? LedgerText.Format(Strings.Forecast_OverdueNote, BillsFormat.ShortDate(e.DueDate)) : string.Empty)).ToList(),
            LedgerText.Money(explanation.Closing.Amount, explanation.Closing.Currency));
    }

    // Consecutive days below the floor as one row each, with the run's lowest day.
    private static IEnumerable<ForecastFloorRow> Runs(ForecastSeriesDto series, string currency)
    {
        var days = series.BelowFloor.OrderBy(p => p.Date).ToList();
        var start = 0;
        for (var i = 1; i <= days.Count; i++)
        {
            if (i < days.Count && days[i].Date.DayNumber == days[i - 1].Date.DayNumber + 1)
            {
                continue;
            }

            var run = days.Skip(start).Take(i - start).ToList();
            var low = run.OrderBy(p => p.Balance.Amount).ThenBy(p => p.Date).First();
            yield return new ForecastFloorRow(run[0].Date, run[^1].Date, series.AccountId, series.Name, low.Date, low.Balance.Amount, currency);
            start = i;
        }
    }

    private static string KindText(ForecastEntryKind kind) => Strings.ResourceManager.GetString("ForecastKind_" + kind, Strings.Culture) ?? kind.ToString();
}
