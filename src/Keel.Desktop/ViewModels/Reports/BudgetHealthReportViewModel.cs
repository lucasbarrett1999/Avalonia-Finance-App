using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Reports;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Reports;

/// <summary>A month-end point of the age-of-money chart and table.</summary>
/// <param name="Date">Point (a month end, or today).</param>
/// <param name="Days">Age of money, or null before any funded outflow.</param>
/// <param name="Outflows">Outflows averaged.</param>
public sealed record AgeOfMoneyRow(DateOnly Date, int? Days, int Outflows)
{
    /// <summary>"Aug 31, 2026".</summary>
    public string DateText => Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>"43 days" or "—".</summary>
    public string DaysText => BudgetHealthReportViewModel.DaysText(Days);

    /// <summary>Opens the month's transactions.</summary>
    public IRelayCommand<AgeOfMoneyRow>? Open { get; init; }
}

/// <summary>An overspent category row (drills to its transactions of the month).</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Name">Name.</param>
/// <param name="GroupName">Group.</param>
/// <param name="Available">Available (negative).</param>
/// <param name="IsCash">Cash overspending (red) rather than credit only (yellow).</param>
/// <param name="Currency">Currency.</param>
public sealed record HealthOverspentRow(Guid CategoryId, string Name, string GroupName, long Available, bool IsCash, string Currency)
{
    /// <summary>Available text (negative).</summary>
    public string AvailableText => ReportFormat.Money(Available, Currency);

    /// <summary>"Cash overspent" or "Credit overspent" (words, not colour alone).</summary>
    public string KindText => IsCash ? Strings.Health_OverspentKindCash : Strings.Health_OverspentKindCredit;

    /// <summary>Screen-reader text.</summary>
    public string AutomationName => LedgerText.Format(Strings.Health_OverspentRowAutomation, Name, AvailableText, KindText);

    /// <summary>Opens the category's transactions of the month.</summary>
    public IRelayCommand<HealthOverspentRow>? Open { get; init; }
}

/// <summary>
/// Budget health (F-REP-5, ADR 0094): age of money with its history over the toolbar range, months ahead,
/// targets funded and overspent categories for the range's last month (at most this month), each with the
/// numbers behind it. Budget-wide: the accounts filter and toggles do not apply.
/// </summary>
public sealed partial class BudgetHealthReportViewModel : ReportViewModel
{
    private readonly TimeProvider _time;

    /// <summary>Creates the report.</summary>
    public BudgetHealthReportViewModel(ReportsViewModel owner, TimeProvider time)
        : base(owner, includeTransfers: true, includeTracking: false)
    {
        _time = time;
    }

    /// <inheritdoc />
    public override ReportKind Kind => ReportKind.BudgetHealth;

    /// <inheritdoc />
    public override string Title => Strings.Health_Title;

    /// <inheritdoc />
    public override string Description => Strings.Health_Description;

    /// <inheritdoc />
    public override string IconKey => "Icon.Report.Health";

    /// <inheritdoc />
    public override bool SupportsTransfers => false;

    /// <inheritdoc />
    public override bool SupportsTracking => false;

    /// <inheritdoc />
    public override bool SupportsAccounts => false;

    /// <inheritdoc />
    public override string CsvFileName => "budget-health.csv";

    /// <summary>The loaded health numbers.</summary>
    public BudgetHealthReport? Report { get; private set; }

    /// <summary>"Budget numbers for September 2026 (as of Sep 25)".</summary>
    [ObservableProperty]
    public partial string MonthHeading { get; private set; } = string.Empty;

    /// <summary>Age of money now, e.g. "43 days".</summary>
    [ObservableProperty]
    public partial string AgeOfMoneyText { get; private set; } = string.Empty;

    /// <summary>What the age of money is based on.</summary>
    [ObservableProperty]
    public partial string AgeOfMoneyDetail { get; private set; } = string.Empty;

    /// <summary>"2.1 months".</summary>
    [ObservableProperty]
    public partial string MonthsAheadText { get; private set; } = string.Empty;

    /// <summary>The division behind months ahead.</summary>
    [ObservableProperty]
    public partial string MonthsAheadDetail { get; private set; } = string.Empty;

    /// <summary>"88%".</summary>
    [ObservableProperty]
    public partial string TargetsText { get; private set; } = string.Empty;

    /// <summary>Counts and amounts behind the percentage.</summary>
    [ObservableProperty]
    public partial string TargetsDetail { get; private set; } = string.Empty;

    /// <summary>Overspent category count.</summary>
    [ObservableProperty]
    public partial string OverspentText { get; private set; } = string.Empty;

    /// <summary>Cash versus credit split, or the all-clear line.</summary>
    [ObservableProperty]
    public partial string OverspentDetail { get; private set; } = string.Empty;

    /// <summary>Whether any category is overspent (warning icon).</summary>
    [ObservableProperty]
    public partial bool HasOverspending { get; private set; }

    /// <summary>Age of money at each month end of the range.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AgeOfMoneyRow> Rows { get; private set; } = [];

    /// <summary>Overspent categories, most overspent first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HealthOverspentRow> Overspent { get; private set; } = [];

    /// <summary>"43 days", "1 day" or "—".</summary>
    public static string DaysText(int? days) => days switch
    {
        null => Strings.Health_NoValue,
        1 => Strings.Health_AgeOfMoneyOneDay,
        _ => LedgerText.Format(Strings.Health_AgeOfMoneyDays, days.Value),
    };

    /// <summary>Opens the transactions of the month ending at a point.</summary>
    [RelayCommand]
    public void OpenRow(AgeOfMoneyRow? row)
    {
        if (row is not null)
        {
            Owner.OpenRegister(new RegisterNavigation(null, ReportFormat.DateSearch(BudgetMonth.Of(row.Date), row.Date), CategoryOption.All));
        }
    }

    /// <summary>Chart click on a point.</summary>
    public void OpenPoint(int index)
    {
        if (index >= 0 && index < Rows.Count)
        {
            OpenRow(Rows[index]);
        }
    }

    /// <summary>Opens an overspent category's transactions of the month.</summary>
    [RelayCommand]
    public void OpenOverspent(HealthOverspentRow? row)
    {
        if (row is not null && Report is { } report)
        {
            Owner.OpenRegister(new RegisterNavigation(null, ReportFormat.DateSearch(report.Month, report.AsOf), new CategoryOption(row.CategoryId, row.Name, row.GroupName)));
        }
    }

    /// <summary>Opens Budget (targets and overspending are fixed there).</summary>
    [RelayCommand]
    public void OpenBudget() => Owner.OpenBudget();

    /// <inheritdoc />
    public override string ToCsv()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { Strings.Health_Csv_Metric, Strings.Health_Csv_Value } };
        if (Report is { } r)
        {
            var currency = r.Currency;
            rows.Add([Strings.Health_Csv_Month, r.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)]);
            rows.Add([Strings.Health_Csv_AsOf, r.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]);
            rows.Add([Strings.Health_Csv_AgeOfMoney, r.AgeOfMoney.Latest?.Days?.ToString(CultureInfo.InvariantCulture) ?? string.Empty]);
            rows.Add([Strings.Health_Csv_MonthsAhead, r.MonthsAhead.Tenths is { } t ? (t / 10m).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty]);
            rows.Add([Strings.Health_Csv_Buffer, ReportFormat.CsvAmount(r.MonthsAhead.Buffer, currency)]);
            rows.Add([Strings.Health_Csv_AverageSpending, ReportFormat.CsvAmount(r.MonthsAhead.AverageSpending, currency)]);
            rows.Add([Strings.Health_Csv_TargetsFunded, r.Targets.Percent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty]);
            rows.Add([Strings.Health_Csv_TargetsCount, LedgerText.Format(Strings.Health_Csv_OfFormat, r.Targets.Funded, r.Targets.Count)]);
            rows.Add([Strings.Health_Csv_Overspent, r.OverspentCount.ToString(CultureInfo.InvariantCulture)]);
            foreach (var point in Rows)
            {
                rows.Add([LedgerText.Format(Strings.Health_Csv_AgeOn, point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), point.Days?.ToString(CultureInfo.InvariantCulture) ?? string.Empty]);
            }
        }

        return ReportFormat.Csv(rows);
    }

    /// <inheritdoc />
    protected override async Task LoadCoreAsync(IReportService service, ReportQuery query, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var month = BudgetMonth.Of(query.To < today ? query.To : today);
        var health = await service.GetBudgetHealthAsync(month, ct);
        var history = await service.GetAgeOfMoneyAsync(query.From, query.To, ct);
        Report = health;
        var currency = health.Currency;
        MonthHeading = LedgerText.Format(Strings.Health_MonthHeading,
            month.ToString("MMMM yyyy", CultureInfo.CurrentCulture), health.AsOf.ToString("MMM d", CultureInfo.CurrentCulture));

        var latest = health.AgeOfMoney.Latest;
        AgeOfMoneyText = DaysText(latest?.Days);
        AgeOfMoneyDetail = latest?.Days is { } days
            ? LedgerText.Format(Strings.Health_AgeOfMoneyDetail, latest.OutflowCount, days)
            : Strings.Health_AgeOfMoneyNone;

        var ahead = health.MonthsAhead;
        MonthsAheadText = ahead.Tenths is { } tenths
            ? LedgerText.Format(Strings.Health_MonthsAheadValue, (tenths / 10m).ToString("0.0", CultureInfo.CurrentCulture))
            : Strings.Health_NoValue;
        MonthsAheadDetail = ahead.Tenths is null
            ? Strings.Health_MonthsAheadNone
            : LedgerText.Format(Strings.Health_MonthsAheadDetail,
                ReportFormat.Money(ahead.Buffer, currency),
                ReportFormat.Money(ahead.AverageSpending, currency),
                ReportFormat.Range(ahead.SpendingFrom, ahead.SpendingTo));

        var targets = health.Targets;
        TargetsText = targets.Percent is { } percent ? LedgerText.Format(Strings.Health_TargetsPercent, percent) : Strings.Health_NoValue;
        TargetsDetail = targets.Count == 0
            ? Strings.Health_TargetsNone
            : LedgerText.Format(Strings.Health_TargetsDetail, targets.Funded, targets.Count,
                ReportFormat.Money(targets.Needed - targets.Underfunded, currency), ReportFormat.Money(targets.Needed, currency));

        OverspentText = health.OverspentCount.ToString(CultureInfo.CurrentCulture);
        HasOverspending = health.OverspentCount > 0;
        OverspentDetail = health.OverspentCount == 0
            ? Strings.Health_OverspentNone
            : LedgerText.Format(Strings.Health_OverspentDetail, health.CashOverspentCount);
        Overspent = health.Overspent
            .Select(o => new HealthOverspentRow(o.CategoryId, o.Name, o.GroupName, o.Available, o.IsCash, currency) { Open = OpenOverspentCommand })
            .ToList();
        Rows = history.Points.Select(p => new AgeOfMoneyRow(p.Date, p.Days, p.OutflowCount) { Open = OpenRowCommand }).ToList();
        HasData = Rows.Any(r => r.Days is not null) || targets.Count > 0 || health.OverspentCount > 0 || ahead.Tenths is not null;
    }
}
