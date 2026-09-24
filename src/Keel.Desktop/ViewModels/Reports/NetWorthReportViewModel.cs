using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Reports;
using Keel.Desktop.Controls;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Reports;

/// <summary>A point row of the net worth table.</summary>
public sealed record NetWorthRow(DateOnly Date, long Assets, long Liabilities, string Currency)
{
    /// <summary>Net worth.</summary>
    public long NetWorth => Assets - Liabilities;

    /// <summary>"Jan 31, 2026".</summary>
    public string DateText => Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>Assets text.</summary>
    public string AssetsText => ReportFormat.Money(Assets, Currency);

    /// <summary>Liabilities text.</summary>
    public string LiabilitiesText => ReportFormat.Money(Liabilities, Currency);

    /// <summary>Net worth text.</summary>
    public string NetWorthText => ReportFormat.Money(NetWorth, Currency);

    /// <summary>Negative net worth.</summary>
    public bool IsNegative => NetWorth < 0;
}

/// <summary>An account band of the stacked area and its legend row.</summary>
public sealed class NetWorthSeriesItem
{
    /// <summary>Creates the item.</summary>
    public NetWorthSeriesItem(IReadOnlyList<Guid> accountIds, string name, int slot, IReadOnlyList<long> balances, bool isLiability, string currency)
    {
        AccountIds = accountIds;
        Name = name;
        Slot = slot;
        Balances = balances;
        IsLiability = isLiability;
        Currency = currency;
    }

    /// <summary>Accounts in the band (more than one for "Other accounts").</summary>
    public IReadOnlyList<Guid> AccountIds { get; }

    /// <summary>Name.</summary>
    public string Name { get; }

    /// <summary>Palette slot.</summary>
    public int Slot { get; }

    /// <summary>Signed balances at each point.</summary>
    public IReadOnlyList<long> Balances { get; }

    /// <summary>Liability (drawn below zero).</summary>
    public bool IsLiability { get; }

    /// <summary>Currency.</summary>
    public string Currency { get; }

    /// <summary>Latest balance text.</summary>
    public string LatestText => Balances.Count == 0 ? string.Empty : ReportFormat.Money(Balances[^1], Currency);

    /// <summary>Accessible name of the drill-down action.</summary>
    public string OpenLabel => LedgerText.Format(Strings.Reports_OpenTransactionsFor, Name);

    /// <summary>Opens the account's register.</summary>
    public IRelayCommand? OpenCommand { get; set; }
}

/// <summary>
/// Net worth (F-REP-3): a line of month-end net worth, optionally with each account as a stacked
/// area (assets above zero, liabilities below). A point opens the register for that month; an
/// account band opens that account's register for that month.
/// </summary>
public sealed partial class NetWorthReportViewModel : ReportViewModel
{
    /// <summary>Creates the report (tracking accounts included by default).</summary>
    public NetWorthReportViewModel(ReportsViewModel owner)
        : base(owner, includeTransfers: true, includeTracking: true)
    {
    }

    /// <inheritdoc />
    public override ReportKind Kind => ReportKind.NetWorth;

    /// <inheritdoc />
    public override string Title => Strings.Reports_NetWorth_Title;

    /// <inheritdoc />
    public override string Description => Strings.Reports_NetWorth_Description;

    /// <inheritdoc />
    public override string IconKey => "Icon.Report.Trend";

    /// <inheritdoc />
    public override bool SupportsTransfers => false;

    /// <inheritdoc />
    public override string CsvFileName => "net-worth.csv";

    /// <summary>The loaded report.</summary>
    public NetWorthReport? Report { get; private set; }

    /// <summary>Point rows, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<NetWorthRow> Rows { get; private set; } = [];

    /// <summary>Account bands (at most eight; the rest fold into "Other accounts").</summary>
    [ObservableProperty]
    public partial IReadOnlyList<NetWorthSeriesItem> Series { get; private set; } = [];

    /// <summary>Show each account as a stacked area.</summary>
    [ObservableProperty]
    public partial bool ShowAccounts { get; set; }

    /// <summary>Latest net worth.</summary>
    [ObservableProperty]
    public partial string LatestText { get; private set; } = string.Empty;

    /// <summary>Whether the latest net worth is negative.</summary>
    [ObservableProperty]
    public partial bool IsLatestNegative { get; private set; }

    /// <summary>Change over the range, e.g. "+$1,200.00 since Oct 31, 2025".</summary>
    [ObservableProperty]
    public partial string ChangeText { get; private set; } = string.Empty;

    /// <summary>Latest assets.</summary>
    [ObservableProperty]
    public partial string AssetsText { get; private set; } = string.Empty;

    /// <summary>Latest liabilities.</summary>
    [ObservableProperty]
    public partial string LiabilitiesText { get; private set; } = string.Empty;

    /// <summary>Opens every transaction of the month ending at a point.</summary>
    [RelayCommand]
    public void OpenRow(NetWorthRow? row)
    {
        if (row is not null)
        {
            Owner.OpenRegister(new RegisterNavigation(Owner.SingleAccountId, ReportFormat.DateSearch(BudgetMonth.Of(row.Date), row.Date), Register.CategoryOption.All));
        }
    }

    /// <summary>Opens an account band's register (the whole range).</summary>
    [RelayCommand]
    public void OpenSeries(NetWorthSeriesItem? item)
    {
        if (item is not null && Query is { } query)
        {
            OpenAccount(item, query.From, Rows.Count > 0 ? Rows[^1].Date : query.To);
        }
    }

    /// <summary>Chart click: <paramref name="series"/> −1 is the net line, otherwise an account band.</summary>
    public void OpenPoint(int series, int index)
    {
        if (index < 0 || index >= Rows.Count)
        {
            return;
        }

        var row = Rows[index];
        if (series >= 0 && series < Series.Count)
        {
            OpenAccount(Series[series], BudgetMonth.Of(row.Date), row.Date);
        }
        else
        {
            OpenRow(row);
        }
    }

    /// <inheritdoc />
    public override string ToCsv()
    {
        var accounts = Report?.Accounts ?? [];
        var header = new List<string> { Strings.Reports_Csv_Date, Strings.Reports_Assets, Strings.Reports_Liabilities, Strings.Reports_NetWorth_Title };
        header.AddRange(accounts.Select(a => a.Name));
        var rows = new List<IReadOnlyList<string>> { header };
        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            var line = new List<string>
            {
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ReportFormat.CsvAmount(r.Assets, r.Currency),
                ReportFormat.CsvAmount(r.Liabilities, r.Currency),
                ReportFormat.CsvAmount(r.NetWorth, r.Currency),
            };
            line.AddRange(accounts.Select(a => ReportFormat.CsvAmount(a.Balances[i], r.Currency)));
            rows.Add(line);
        }

        return ReportFormat.Csv(rows);
    }

    /// <inheritdoc />
    protected override async Task LoadCoreAsync(IReportService service, ReportQuery query, CancellationToken ct)
    {
        var report = await service.GetNetWorthAsync(query, ct);
        Report = report;
        var currency = report.Currency;
        Rows = report.Points.Select(p => new NetWorthRow(p.Date, p.Assets, p.Liabilities, currency)).ToList();
        HasData = report.Points.Count > 0 && report.Accounts.Any(a => a.Balances.Any(b => b != 0));

        // Stable slots by account order; accounts past the palette fold into "Other accounts".
        var visible = report.Accounts.Where(a => a.Balances.Any(b => b != 0)).ToList();
        var bands = new List<NetWorthSeriesItem>();
        var head = visible.Count <= ChartPalette.SlotCount ? visible : visible.Take(ChartPalette.SlotCount - 1).ToList();
        for (var i = 0; i < head.Count; i++)
        {
            bands.Add(new NetWorthSeriesItem([head[i].AccountId], head[i].Name, i, head[i].Balances, head[i].IsLiability, currency));
        }

        foreach (var isLiability in new[] { false, true })
        {
            var rest = visible.Skip(head.Count).Where(a => a.IsLiability == isLiability).ToList();
            if (rest.Count > 0)
            {
                var sums = Enumerable.Range(0, report.Points.Count).Select(p => rest.Sum(a => a.Balances[p])).ToList();
                bands.Add(new NetWorthSeriesItem([.. rest.Select(a => a.AccountId)], Strings.Reports_OtherAccounts, ChartPalette.OtherSlot, sums, isLiability, currency));
            }
        }

        foreach (var band in bands)
        {
            band.OpenCommand = OpenSeriesCommand;
        }

        Series = bands;
        if (Rows.Count > 0)
        {
            var first = Rows[0];
            var last = Rows[^1];
            LatestText = last.NetWorthText;
            IsLatestNegative = last.IsNegative;
            AssetsText = last.AssetsText;
            LiabilitiesText = last.LiabilitiesText;
            var change = last.NetWorth - first.NetWorth;
            ChangeText = LedgerText.Format(change >= 0 ? Strings.Reports_NetWorthUp : Strings.Reports_NetWorthDown,
                ReportFormat.Money(Math.Abs(change), currency), first.DateText);
        }
        else
        {
            LatestText = AssetsText = LiabilitiesText = ChangeText = string.Empty;
            IsLatestNegative = false;
        }
    }

    partial void OnShowAccountsChanged(bool value) => RaiseChartChanged();

    private void OpenAccount(NetWorthSeriesItem item, DateOnly from, DateOnly to)
    {
        var search = ReportFormat.DateSearch(from, to);
        Owner.OpenRegister(item.AccountIds.Count == 1
            ? new RegisterNavigation(item.AccountIds[0], search, Register.CategoryOption.All)
            : new RegisterNavigation(null, search, Register.CategoryOption.All));
    }
}
