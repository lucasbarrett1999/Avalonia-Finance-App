using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Budget;
using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The Budget screen's alternate presentations: the three-month side-by-side grid (F-BUD-2 P1, ADR 0090)
/// and the Flex view (F-BUD-6, ADR 0091). Both show months that are already computed from the loaded
/// ledger data (ADR 0041); switching never reloads the ledger. In three-month mode
/// <see cref="CurrentMonth"/> is the month the cell cursor is in (every command acts on it) and
/// <see cref="MonthOffset"/> its position in the visible window.
/// </summary>
public sealed partial class BudgetViewModel
{
    /// <summary>Months shown side by side in three-month mode.</summary>
    public const int WindowMonths = 3;

    private readonly IAppSettingsStore _settings;

    /// <summary>Three months side by side (W).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowStart), nameof(ThreeMonthsTip), nameof(MonthTitle))]
    public partial bool IsThreeMonths { get; private set; }

    /// <summary>The Flex view instead of the grid (F).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTable), nameof(ShowFlex), nameof(FlexViewTip))]
    public partial bool IsFlexView { get; private set; }

    /// <summary>Position of <see cref="CurrentMonth"/> in the three-month window (0 in single-month mode).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowStart), nameof(MonthTitle))]
    public partial int MonthOffset { get; private set; }

    /// <summary>First visible month.</summary>
    public DateOnly WindowStart => BudgetMonth.Add(CurrentMonth, -MonthOffset);

    /// <summary>The visible months (one, or three side by side).</summary>
    public IReadOnlyList<DateOnly> VisibleMonths => IsThreeMonths
        ? [WindowStart, BudgetMonth.Add(WindowStart, 1), BudgetMonth.Add(WindowStart, 2)]
        : [CurrentMonth];

    /// <summary>Column headers of the three-month grid: month and Ready to Assign.</summary>
    public IReadOnlyList<BudgetMonthHeaderViewModel> MonthHeaders { get; private set; } = [];

    /// <summary>The Flex view of <see cref="CurrentMonth"/>.</summary>
    public BudgetFlexViewModel Flex { get; private set; } = null!;

    /// <summary>The grid shows only the categories of one Flex kind (a Flex-view drill-down), or everything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlexFilter), nameof(FlexFilterText))]
    public partial FlexKind? FlexFilter { get; private set; }

    /// <summary>Whether a Flex filter is on.</summary>
    public bool HasFlexFilter => FlexFilter is not null;

    /// <summary>"Showing Fixed categories".</summary>
    public string FlexFilterText => FlexFilter is { } kind ? LedgerText.Format(Strings.Flex_FilterBanner, BudgetText.FlexKind(kind)) : string.Empty;

    /// <summary>Whether the grid (single or three months) is shown.</summary>
    public bool ShowTable => ShowGrid && !IsFlexView;

    /// <summary>Whether the Flex view is shown.</summary>
    public bool ShowFlex => ShowGrid && IsFlexView;

    /// <summary>Tooltip of the three-month toggle.</summary>
    public string ThreeMonthsTip => $"{Strings.BudgetMonths_Toggle} ({Shortcuts.Format(Shortcuts.ToggleThreeMonths)})";

    /// <summary>Tooltip of the Flex view toggle.</summary>
    public string FlexViewTip => $"{Strings.Flex_Toggle} ({Shortcuts.Format(Shortcuts.ToggleFlexView)})";

    /// <summary>Shows or hides the three-month view (W); the choice is remembered.</summary>
    [RelayCommand]
    public void ToggleThreeMonths() => SetThreeMonths(!IsThreeMonths);

    /// <summary>Turns the three-month view on or off. It shows the grid, so the Flex view closes.</summary>
    public void SetThreeMonths(bool on)
    {
        if (on == IsThreeMonths && !IsFlexView)
        {
            return;
        }

        CommitOpenEdit();
        IsThreeMonths = on;
        MonthOffset = 0;
        IsFlexView = false;
        SaveViewSettings();
        ShowCurrent(rebuildRows: true);
        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Switches between the grid and the Flex view (F); the choice is remembered.</summary>
    [RelayCommand]
    public void ToggleFlexView() => SetFlexView(!IsFlexView);

    /// <summary>Shows the Flex view or the grid. The Flex view always shows one month.</summary>
    public void SetFlexView(bool on)
    {
        CommitOpenEdit();
        if (on == IsFlexView)
        {
            return;
        }

        IsFlexView = on;
        if (on)
        {
            FlexFilter = null;
            if (IsThreeMonths)
            {
                IsThreeMonths = false;
                MonthOffset = 0;
            }
        }

        SaveViewSettings();
        ShowCurrent(rebuildRows: true);
        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Flex-view drill-down: the grid with only the categories of <paramref name="kind"/>.</summary>
    public void ShowFlexCategories(FlexKind kind)
    {
        IsFlexView = false;
        SaveViewSettings();
        FlexFilter = kind;
        Flatten(force: true);
        var first = Rows.OfType<BudgetCategoryRowViewModel>().FirstOrDefault();
        Select(first ?? Rows.FirstOrDefault(), BudgetColumn.Assigned);
        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows every category again.</summary>
    [RelayCommand]
    public void ClearFlexFilter()
    {
        if (FlexFilter is null)
        {
            return;
        }

        FlexFilter = null;
        Flatten(force: true);
        if (SelectedRow is { } row)
        {
            Select(row, SelectedColumn);
        }

        FocusGridRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the cursor into another visible month (three-month mode), keeping the window.</summary>
    public void SelectMonth(int offset)
    {
        if (!IsThreeMonths)
        {
            return;
        }

        offset = Math.Clamp(offset, 0, WindowMonths - 1);
        if (offset == MonthOffset)
        {
            return;
        }

        var start = WindowStart;
        CommitOpenEdit();
        MonthOffset = offset;
        GoToMonth(BudgetMonth.Add(start, offset));
    }

    /// <summary>Selects a cell of a visible month (clicks in the three-month grid).</summary>
    public void SelectCell(BudgetRowViewModel? row, BudgetColumn column, int offset)
    {
        SelectMonth(offset);
        Select(row, column);
    }

    /// <summary>Opens the Activity of a category in a visible month.</summary>
    public void OpenActivity(BudgetCategoryRowViewModel? row, int offset)
    {
        SelectMonth(offset);
        OpenActivity(row);
    }

    /// <summary>Flex-view drill-down for income: this month's Ready to Assign transactions in the register.</summary>
    public void OpenIncome()
    {
        var search = "date:" + CurrentMonth.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(null, search,
            new Keel.Desktop.ViewModels.Register.CategoryOption(Keel.Domain.Entities.SystemIds.ReadyToAssignCategory, Strings.Budget_ReadyToAssign, Strings.Flex_InflowGroup)));
    }

    /// <summary>Tags a category Fixed, Non-monthly or Flex (Unset: automatic), as one undoable action (F-BUD-6).</summary>
    public async Task SetFlexTagAsync(Guid categoryId, FlexKind kind)
    {
        var row = FindCategory(categoryId);
        if (row is null || row.IsCardPayment || row.FlexTag == kind)
        {
            return;
        }

        var failed = false;
        await QueueWrite(async () =>
        {
            try
            {
                await _categories.SetFlexKindAsync(categoryId, kind, CancellationToken.None);
            }
            catch (Keel.Application.Ledger.LedgerValidationException)
            {
                failed = true;
                throw;
            }
        });
        if (!failed)
        {
            _status.Show(LedgerText.Format(Strings.Flex_Tagged, row.Name, BudgetText.FlexKind(kind)), offerUndo: true);
        }
    }

    /// <summary>Whether the loaded data holds every visible month around <paramref name="current"/>.</summary>
    private bool Covers(DateOnly current)
    {
        if (_ledger is not { } ledger)
        {
            return false;
        }

        var start = BudgetMonth.Add(current, -MonthOffset);
        var end = IsThreeMonths ? BudgetMonth.Add(start, WindowMonths - 1) : current;
        return ledger.Covers(start) && ledger.Covers(end);
    }

    private bool IsShownLoaded(DateOnly current) =>
        Covers(current) && _months.ContainsKey(current)
        && (!IsThreeMonths || Enumerable.Range(0, WindowMonths).All(i => _months.ContainsKey(BudgetMonth.Add(current, i - MonthOffset))));

    private void CommitOpenEdit()
    {
        if (EditingRow is { } editing)
        {
            CommitEdit(editing, 0, keepEditing: false);
        }
    }

    // Shows the current month in the current mode: in memory when loaded, otherwise loads a range.
    private void ShowCurrent(bool rebuildRows)
    {
        if (rebuildRows)
        {
            // New containers: the view picks the row templates of the mode when the rows come back.
            Rows.Clear();
            Flatten(force: true);
        }

        if (_ledger is not null && IsShownLoaded(CurrentMonth))
        {
            Apply();
            if (SelectedRow is { } row)
            {
                Select(row, SelectedColumn);
            }
        }
        else
        {
            Invalidate(ledger: true);
        }
    }

    private void SaveViewSettings()
    {
        var three = IsThreeMonths;
        var flex = IsFlexView;
        try
        {
            _settings.Update(s => s with { BudgetThreeMonths = three, BudgetFlexView = flex });
        }
        catch (IOException)
        {
            // Remembering the view is a convenience; the screen keeps working without it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Month headers, the month cells of every row, and the Flex view (after Apply).
    private void ApplyViews()
    {
        Flex.Update(Month, DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
        if (!IsThreeMonths)
        {
            return;
        }

        var start = WindowStart;
        var months = Enumerable.Range(0, WindowMonths).Select(i => _months.GetValueOrDefault(BudgetMonth.Add(start, i))).ToList();
        if (MonthHeaders.Count != WindowMonths)
        {
            MonthHeaders = [.. Enumerable.Range(0, WindowMonths).Select(i => new BudgetMonthHeaderViewModel(this, i))];
            OnPropertyChanged(nameof(MonthHeaders));
        }

        var groupCells = months.Select(m => m?.Groups.ToDictionary(g => g.Id)).ToList();
        var categoryCells = months.Select(m => m?.Groups.SelectMany(g => g.Categories).ToDictionary(c => c.Id)).ToList();
        for (var i = 0; i < WindowMonths; i++)
        {
            var month = BudgetMonth.Add(start, i);
            var active = i == MonthOffset;
            MonthHeaders[i].Update(month, active, months[i]);
            foreach (var group in _groups)
            {
                group.Months[i].Update(month, active, groupCells[i]?.GetValueOrDefault(group.Id));
                foreach (var category in group.Children)
                {
                    category.Months[i].Update(month, active, categoryCells[i]?.GetValueOrDefault(category.Id));
                }
            }
        }
    }

    // Rows of the grid under the Flex filter: matching categories and the groups that hold them.
    private bool IsShown(BudgetCategoryRowViewModel row) => FlexFilter is not { } kind || row.Flex == kind;
}
