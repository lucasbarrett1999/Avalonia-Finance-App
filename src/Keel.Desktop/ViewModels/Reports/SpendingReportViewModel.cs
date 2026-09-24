using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Reports;
using Keel.Desktop.Controls;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Reports;

/// <summary>What a spending item stands for.</summary>
public enum SpendingItemKind
{
    /// <summary>A category group (drills to its categories).</summary>
    Group,

    /// <summary>A category (drills to the register).</summary>
    Category,

    /// <summary>Rows without a category (drills to the register for the range).</summary>
    Uncategorized,

    /// <summary>The folded tail beyond the eight palette slots (drills to its items).</summary>
    Other,
}

/// <summary>A slice of the spending donut and a row of its table.</summary>
public sealed class SpendingItem
{
    /// <summary>Creates an item.</summary>
    public SpendingItem(SpendingItemKind kind, string name, long amount, long previous, string currency, Guid? id, string? groupName, IReadOnlyList<SpendingItem> children)
    {
        Kind = kind;
        Name = name;
        Amount = amount;
        Previous = previous;
        Currency = currency;
        Id = id;
        GroupName = groupName;
        Children = children;
    }

    /// <summary>Kind.</summary>
    public SpendingItemKind Kind { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Spending in the period.</summary>
    public long Amount { get; }

    /// <summary>Spending in the previous period.</summary>
    public long Previous { get; }

    /// <summary>Currency.</summary>
    public string Currency { get; }

    /// <summary>Group or category id.</summary>
    public Guid? Id { get; }

    /// <summary>Group name of a category.</summary>
    public string? GroupName { get; }

    /// <summary>Items one level down (groups: categories; Other: the folded items).</summary>
    public IReadOnlyList<SpendingItem> Children { get; }

    /// <summary>Palette slot; <see cref="ChartPalette.OtherSlot"/> when folded or not drawn.</summary>
    public int Slot { get; set; } = ChartPalette.OtherSlot;

    /// <summary>Whether the item is a slice of the donut (positive spending).</summary>
    public bool IsSlice { get; set; }

    /// <summary>Share of the donut, e.g. "12.5%".</summary>
    public string ShareText { get; set; } = string.Empty;

    /// <summary>Amount text.</summary>
    public string AmountText => ReportFormat.Money(Amount, Currency);

    /// <summary>Previous-period amount text.</summary>
    public string PreviousText => ReportFormat.Money(Previous, Currency);

    /// <summary>Change versus the previous period.</summary>
    public string ChangeText => ReportFormat.Change(Amount, Previous);

    /// <summary>Refunds exceed spending (shown in the table, not in the donut).</summary>
    public bool IsNetRefund => Amount < 0;

    /// <summary>Accessible name of the drill-down action.</summary>
    public string OpenLabel => LedgerText.Format(Kind is SpendingItemKind.Category or SpendingItemKind.Uncategorized ? Strings.Reports_OpenTransactionsFor : Strings.Reports_DrillInto, Name);

    /// <summary>Drills into the item (bound by the table row; the chart calls it too).</summary>
    public IRelayCommand? OpenCommand { get; set; }
}

/// <summary>
/// Spending (F-REP-1): a donut by group that drills to categories and then to the All Accounts
/// register filtered by category and range; a table with the previous period beside it.
/// </summary>
public sealed partial class SpendingReportViewModel : ReportViewModel
{
    private readonly List<(string Title, IReadOnlyList<SpendingItem> Items, string Key)> _levels = [];

    /// <summary>Creates the report (transfers counted, tracking accounts excluded by default).</summary>
    public SpendingReportViewModel(ReportsViewModel owner)
        : base(owner, includeTransfers: true, includeTracking: false)
    {
    }

    /// <inheritdoc />
    public override ReportKind Kind => ReportKind.Spending;

    /// <inheritdoc />
    public override string Title => Strings.Reports_Spending_Title;

    /// <inheritdoc />
    public override string Description => Strings.Reports_Spending_Description;

    /// <inheritdoc />
    public override string IconKey => "Icon.Report.Pie";

    /// <inheritdoc />
    public override string CsvFileName => "spending.csv";

    /// <summary>The loaded report.</summary>
    public SpendingReport? Report { get; private set; }

    /// <summary>Items at the current level, largest first (the table).</summary>
    public ObservableCollection<SpendingItem> Items { get; } = [];

    /// <summary>Donut slices at the current level (at most eight, the tail folded into Other).</summary>
    public IReadOnlyList<SpendingItem> Slices { get; private set; } = [];

    /// <summary>Breadcrumb titles, root first.</summary>
    public ObservableCollection<string> Crumbs { get; } = [];

    /// <summary>"All groups › Everyday".</summary>
    [ObservableProperty]
    public partial string BreadcrumbText { get; private set; } = string.Empty;

    /// <summary>Title of the current level.</summary>
    [ObservableProperty]
    public partial string LevelTitle { get; private set; } = string.Empty;

    /// <summary>Whether a level above exists.</summary>
    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    /// <summary>Total spending at the current level.</summary>
    [ObservableProperty]
    public partial string TotalText { get; private set; } = string.Empty;

    /// <summary>Previous-period total at the current level.</summary>
    [ObservableProperty]
    public partial string PreviousTotalText { get; private set; } = string.Empty;

    /// <summary>Change of the current level's total.</summary>
    [ObservableProperty]
    public partial string ChangeText { get; private set; } = string.Empty;

    /// <summary>"Compared with Jul 1, 2026 – Jul 31, 2026".</summary>
    [ObservableProperty]
    public partial string PreviousPeriodText { get; private set; } = string.Empty;

    /// <summary>Whether the donut has any slice.</summary>
    [ObservableProperty]
    public partial bool HasSlices { get; private set; }

    /// <summary>Opens an item: a level down, or the register.</summary>
    [RelayCommand]
    public void Open(SpendingItem? item)
    {
        if (item is null || Report is null)
        {
            return;
        }

        switch (item.Kind)
        {
            case SpendingItemKind.Group when item.Children.Count == 1 && item.Children[0].Kind == SpendingItemKind.Uncategorized:
                Open(item.Children[0]);
                break;
            case SpendingItemKind.Group:
            case SpendingItemKind.Other:
                _levels.Add((item.Name, item.Children, item.Kind == SpendingItemKind.Other ? "other" : item.Id?.ToString() ?? "none"));
                ShowLevel();
                break;
            case SpendingItemKind.Category:
                Owner.OpenRegister(new RegisterNavigation(Owner.SingleAccountId, ReportFormat.DateSearch(Report.From, Report.To),
                    new Register.CategoryOption(item.Id, item.Name, item.GroupName ?? string.Empty)));
                break;
            case SpendingItemKind.Uncategorized:
                Owner.OpenRegister(new RegisterNavigation(Owner.SingleAccountId, ReportFormat.DateSearch(Report.From, Report.To), Register.CategoryOption.All));
                break;
        }
    }

    /// <summary>Goes up one level.</summary>
    [RelayCommand]
    public void Back()
    {
        if (_levels.Count > 1)
        {
            _levels.RemoveAt(_levels.Count - 1);
            ShowLevel();
        }
    }

    /// <summary>Opens the donut slice at <paramref name="index"/> (chart click).</summary>
    public void OpenSlice(int index)
    {
        if (index >= 0 && index < Slices.Count)
        {
            Open(Slices[index]);
        }
    }

    /// <inheritdoc />
    public override string ToCsv()
    {
        var report = Report;
        var rows = new List<IReadOnlyList<string>> { new[] { Strings.Reports_Csv_Group, Strings.Reports_Csv_Category, Strings.Reports_Csv_Amount, Strings.Reports_Csv_Previous } };
        if (report is not null)
        {
            foreach (var group in report.Groups)
            {
                foreach (var category in group.Categories)
                {
                    rows.Add([group.Name ?? Strings.Reports_Uncategorized, category.Name ?? Strings.Reports_Uncategorized,
                        ReportFormat.CsvAmount(category.Amount, report.Currency), ReportFormat.CsvAmount(category.PreviousAmount, report.Currency)]);
                }
            }

            rows.Add([Strings.Reports_Csv_Total, string.Empty, ReportFormat.CsvAmount(report.Total, report.Currency), ReportFormat.CsvAmount(report.PreviousTotal, report.Currency)]);
        }

        return ReportFormat.Csv(rows);
    }

    /// <inheritdoc />
    protected override async Task LoadCoreAsync(IReportService service, ReportQuery query, CancellationToken ct)
    {
        var report = await service.GetSpendingAsync(query, ct);
        var path = _levels.Skip(1).Select(l => l.Key).ToList();
        Report = report;
        HasData = report.Groups.Count > 0;
        PreviousPeriodText = LedgerText.Format(Strings.Reports_ComparedWith, ReportFormat.Range(report.PreviousFrom, report.PreviousTo));

        var root = report.Groups.Select(g => ToItem(g, report.Currency)).ToList();
        _levels.Clear();
        _levels.Add((Strings.Reports_AllGroups, root, "root"));

        // Keep the drill path across refreshes when the same groups still exist.
        foreach (var key in path)
        {
            var current = WithOther(_levels[^1].Items);
            var next = current.FirstOrDefault(i => (i.Kind == SpendingItemKind.Other ? "other" : i.Id?.ToString() ?? "none") == key && i.Children.Count > 0);
            if (next is null)
            {
                break;
            }

            _levels.Add((next.Name, next.Children, key));
        }

        ShowLevel();
    }

    private static SpendingItem ToItem(SpendingGroup group, string currency)
    {
        if (group.IsUncategorized)
        {
            return new SpendingItem(SpendingItemKind.Group, Strings.Reports_Uncategorized, group.Amount, group.PreviousAmount, currency, null, null,
                [new SpendingItem(SpendingItemKind.Uncategorized, Strings.Reports_Uncategorized, group.Amount, group.PreviousAmount, currency, null, null, [])]);
        }

        var categories = group.Categories
            .Select(c => new SpendingItem(SpendingItemKind.Category, c.Name ?? string.Empty, c.Amount, c.PreviousAmount, currency, c.CategoryId, group.Name, []))
            .ToList();
        return new SpendingItem(SpendingItemKind.Group, group.Name ?? string.Empty, group.Amount, group.PreviousAmount, currency, group.GroupId, null, categories);
    }

    // The donut: positive items largest first; past eight, the tail folds into "Other".
    private static List<SpendingItem> WithOther(IReadOnlyList<SpendingItem> items)
    {
        var positive = items.Where(i => i.Amount > 0).OrderByDescending(i => i.Amount).ToList();
        if (positive.Count <= ChartPalette.SlotCount)
        {
            return positive;
        }

        var head = positive.Take(ChartPalette.SlotCount - 1).ToList();
        var tail = positive.Skip(ChartPalette.SlotCount - 1).ToList();
        var currency = tail[0].Currency;
        head.Add(new SpendingItem(SpendingItemKind.Other, Strings.Reports_Other, tail.Sum(i => i.Amount), tail.Sum(i => i.Previous), currency, null, null, tail));
        return head;
    }

    private void ShowLevel()
    {
        var (title, items, _) = _levels[^1];
        var slices = WithOther(items);
        var total = slices.Sum(s => s.Amount);
        for (var i = 0; i < slices.Count; i++)
        {
            slices[i].Slot = slices[i].Kind == SpendingItemKind.Other ? ChartPalette.OtherSlot : i;
            slices[i].IsSlice = true;
            slices[i].ShareText = ReportFormat.Share(slices[i].Amount, total);
        }

        var folded = slices.FirstOrDefault(s => s.Kind == SpendingItemKind.Other)?.Children ?? [];
        foreach (var item in items)
        {
            item.OpenCommand = OpenCommand;
            if (!slices.Contains(item))
            {
                item.Slot = ChartPalette.OtherSlot;
                item.IsSlice = false;
                item.ShareText = folded.Contains(item) ? ReportFormat.Share(item.Amount, total) : string.Empty;
            }
        }

        foreach (var slice in slices)
        {
            slice.OpenCommand = OpenCommand;
        }

        Slices = slices;
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        var currency = Report?.Currency ?? string.Empty;
        var sum = items.Sum(i => i.Amount);
        var previous = items.Sum(i => i.Previous);
        LevelTitle = title;
        TotalText = ReportFormat.Money(sum, currency);
        PreviousTotalText = ReportFormat.Money(previous, currency);
        ChangeText = ReportFormat.Change(sum, previous);
        HasSlices = slices.Count > 0;
        CanGoBack = _levels.Count > 1;
        Crumbs.Clear();
        foreach (var level in _levels)
        {
            Crumbs.Add(level.Title);
        }

        BreadcrumbText = string.Join(" › ", Crumbs);

        RaiseChartChanged();
    }
}
