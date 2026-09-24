using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Reports;
using Keel.Desktop.Resources;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain.Entities;
using Keel.Domain.Reports;

namespace Keel.Desktop.ViewModels.Reports;

/// <summary>A month row of the income versus expense table.</summary>
/// <param name="Month">First day of the month.</param>
/// <param name="From">First day of the month inside the report range.</param>
/// <param name="To">Last day of the month inside the report range.</param>
/// <param name="Income">Income.</param>
/// <param name="Expense">Expense.</param>
/// <param name="Currency">Currency.</param>
public sealed record IncomeExpenseRow(DateOnly Month, DateOnly From, DateOnly To, long Income, long Expense, string Currency)
{
    /// <summary>Net.</summary>
    public long Net => Income - Expense;

    /// <summary>"Jan 2026".</summary>
    public string MonthText => ReportFormat.MonthLabel(Month);

    /// <summary>Income text.</summary>
    public string IncomeText => ReportFormat.Money(Income, Currency);

    /// <summary>Expense text.</summary>
    public string ExpenseText => ReportFormat.Money(Expense, Currency);

    /// <summary>Net text.</summary>
    public string NetText => ReportFormat.Money(Net, Currency);

    /// <summary>Whether more went out than came in (red, with the minus sign).</summary>
    public bool IsNetNegative => Net < 0;
}

/// <summary>
/// Income versus expense (F-REP-2): monthly income and expense bars with a net line and a table.
/// An income bar opens the register (Ready to Assign, that month); an expense bar opens the
/// Spending report for that month; a net point opens the register for that month.
/// </summary>
public sealed partial class IncomeExpenseReportViewModel : ReportViewModel
{
    /// <summary>Creates the report (transfers counted, tracking accounts excluded by default).</summary>
    public IncomeExpenseReportViewModel(ReportsViewModel owner)
        : base(owner, includeTransfers: true, includeTracking: false)
    {
    }

    /// <inheritdoc />
    public override ReportKind Kind => ReportKind.IncomeExpense;

    /// <inheritdoc />
    public override string Title => Strings.Reports_IncomeExpense_Title;

    /// <inheritdoc />
    public override string Description => Strings.Reports_IncomeExpense_Description;

    /// <inheritdoc />
    public override string IconKey => "Icon.Reports";

    /// <inheritdoc />
    public override string CsvFileName => "income-vs-expense.csv";

    /// <summary>The loaded report.</summary>
    public IncomeExpenseReport? Report { get; private set; }

    /// <summary>Month rows.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<IncomeExpenseRow> Rows { get; private set; } = [];

    /// <summary>Total income.</summary>
    [ObservableProperty]
    public partial string TotalIncomeText { get; private set; } = string.Empty;

    /// <summary>Total expense.</summary>
    [ObservableProperty]
    public partial string TotalExpenseText { get; private set; } = string.Empty;

    /// <summary>Total net.</summary>
    [ObservableProperty]
    public partial string TotalNetText { get; private set; } = string.Empty;

    /// <summary>Whether the total net is negative.</summary>
    [ObservableProperty]
    public partial bool IsTotalNetNegative { get; private set; }

    /// <summary>Average net per month.</summary>
    [ObservableProperty]
    public partial string AverageNetText { get; private set; } = string.Empty;

    /// <summary>Opens the income transactions of a month (Ready to Assign in the register).</summary>
    [RelayCommand]
    public void OpenIncome(IncomeExpenseRow? row)
    {
        if (row is not null)
        {
            Owner.OpenRegister(new RegisterNavigation(Owner.SingleAccountId, ReportFormat.DateSearch(row.From, row.To),
                new CategoryOption(SystemIds.ReadyToAssignCategory, Strings.Reports_ReadyToAssign, Strings.Reports_InflowGroup)));
        }
    }

    /// <summary>Opens the Spending report for a month (the expense breakdown).</summary>
    [RelayCommand]
    public void OpenExpense(IncomeExpenseRow? row)
    {
        if (row is not null)
        {
            Owner.ShowSpending(row.From, row.To, IncludeTransfers, IncludeTracking);
        }
    }

    /// <summary>Opens every transaction of a month in the register.</summary>
    [RelayCommand]
    public void OpenMonth(IncomeExpenseRow? row)
    {
        if (row is not null)
        {
            Owner.OpenRegister(new RegisterNavigation(Owner.SingleAccountId, ReportFormat.DateSearch(row.From, row.To), CategoryOption.All));
        }
    }

    /// <summary>Chart click: series 0 income, 1 expense, 2 net; <paramref name="index"/> is the month.</summary>
    public void OpenPoint(int series, int index)
    {
        if (index < 0 || index >= Rows.Count)
        {
            return;
        }

        var row = Rows[index];
        switch (series)
        {
            case 0:
                OpenIncome(row);
                break;
            case 1:
                OpenExpense(row);
                break;
            default:
                OpenMonth(row);
                break;
        }
    }

    /// <inheritdoc />
    public override string ToCsv()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { Strings.Reports_Csv_Month, Strings.Reports_Income, Strings.Reports_Expense, Strings.Reports_Net } };
        if (Report is { } report)
        {
            rows.AddRange(Rows.Select(r => (IReadOnlyList<string>)[r.Month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                ReportFormat.CsvAmount(r.Income, r.Currency), ReportFormat.CsvAmount(r.Expense, r.Currency), ReportFormat.CsvAmount(r.Net, r.Currency)]));
            rows.Add([Strings.Reports_Csv_Total, ReportFormat.CsvAmount(report.TotalIncome, report.Currency),
                ReportFormat.CsvAmount(report.TotalExpense, report.Currency), ReportFormat.CsvAmount(report.TotalNet, report.Currency)]);
        }

        return ReportFormat.Csv(rows);
    }

    /// <inheritdoc />
    protected override async Task LoadCoreAsync(IReportService service, ReportQuery query, CancellationToken ct)
    {
        var report = await service.GetIncomeExpenseAsync(query, ct);
        Report = report;
        Rows = report.Months.Select(m =>
        {
            var from = m.Month < query.From ? query.From : m.Month;
            var end = ReportPeriod.MonthEnd(m.Month);
            return new IncomeExpenseRow(m.Month, from, end > query.To ? query.To : end, m.Income, m.Expense, report.Currency);
        }).ToList();
        HasData = report.Months.Any(m => m.Income != 0 || m.Expense != 0);
        TotalIncomeText = ReportFormat.Money(report.TotalIncome, report.Currency);
        TotalExpenseText = ReportFormat.Money(report.TotalExpense, report.Currency);
        TotalNetText = ReportFormat.Money(report.TotalNet, report.Currency);
        IsTotalNetNegative = report.TotalNet < 0;
        AverageNetText = ReportFormat.Money(report.Months.Count == 0 ? 0 : report.TotalNet / report.Months.Count, report.Currency);
    }
}
