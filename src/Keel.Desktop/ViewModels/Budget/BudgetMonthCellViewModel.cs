using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Budget;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>
/// One month of a grid row in the three-month view (F-BUD-2 P1, ADR 0090). The row's own numbers stay
/// those of the active month (the month the cell cursor is in); each row has three of these cells for the
/// visible window. The cursor, the editor and the pill colours follow the active cell.
/// </summary>
public sealed partial class BudgetMonthCellViewModel : ObservableObject
{
    /// <summary>Creates the cell at <paramref name="offset"/> (0–2) of the window.</summary>
    public BudgetMonthCellViewModel(BudgetRowViewModel row, int offset)
    {
        Row = row;
        Offset = offset;
    }

    /// <summary>The row.</summary>
    public BudgetRowViewModel Row { get; }

    /// <summary>The category row (the Assigned editor binds to it), or null for a group row.</summary>
    public BudgetCategoryRowViewModel? Category => Row as BudgetCategoryRowViewModel;

    /// <summary>Position in the three-month window.</summary>
    public int Offset { get; }

    /// <summary>The month (first day).</summary>
    [ObservableProperty]
    public partial DateOnly Month { get; private set; }

    /// <summary>Assigned text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string AssignedText { get; private set; } = string.Empty;

    /// <summary>Activity text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string ActivityText { get; private set; } = string.Empty;

    /// <summary>Available text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string AvailableText { get; private set; } = string.Empty;

    /// <summary>Available in minor units.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPositive), nameof(IsCreditOverspent), nameof(IsCashOverspent), nameof(AvailableStateText), nameof(AutomationName))]
    public partial long Available { get; private set; }

    /// <summary>Overspending colour (6.4.4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreditOverspent), nameof(IsCashOverspent), nameof(AvailableStateText), nameof(AutomationName))]
    public partial OverspendingKind Overspending { get; private set; }

    /// <summary>Whether this is the month the cell cursor is in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssignedSelected), nameof(IsActivitySelected), nameof(IsAvailableSelected), nameof(IsEditing))]
    public partial bool IsActive { get; private set; }

    /// <summary>Available &gt; 0 (green pill).</summary>
    public bool IsPositive => Available > 0;

    /// <summary>Credit-only overspending (yellow pill).</summary>
    public bool IsCreditOverspent => Available < 0 && Overspending == OverspendingKind.Credit;

    /// <summary>Cash overspending (red pill).</summary>
    public bool IsCashOverspent => Available < 0 && !IsCreditOverspent;

    /// <summary>Screen-reader text for the pill state.</summary>
    public string AvailableStateText => IsCashOverspent ? Strings.Budget_StateCashOverspent
        : IsCreditOverspent ? Strings.Budget_StateCreditOverspent
        : IsPositive ? Strings.Budget_StateFunded
        : Strings.Budget_StateEmpty;

    /// <summary>Assigned cell holds the cursor.</summary>
    public bool IsAssignedSelected => IsActive && Row.SelectedColumn == BudgetColumn.Assigned;

    /// <summary>Activity cell holds the cursor.</summary>
    public bool IsActivitySelected => IsActive && Row.SelectedColumn == BudgetColumn.Activity;

    /// <summary>Available cell holds the cursor.</summary>
    public bool IsAvailableSelected => IsActive && Row.SelectedColumn == BudgetColumn.Available;

    /// <summary>The Assigned editor is open in this cell.</summary>
    public bool IsEditing => IsActive && Category is { IsEditing: true };

    /// <summary>"September 2026: assigned $1.00, activity $2.00, available $3.00".</summary>
    public string AutomationName => LedgerText.Format(Strings.BudgetMonths_CellAutomation, BudgetText.Month(Month), AssignedText, ActivityText, AvailableText, AvailableStateText);

    /// <summary>Takes a category's numbers of <paramref name="month"/>.</summary>
    public void Update(DateOnly month, bool active, BudgetCategoryDto? category)
    {
        Month = month;
        IsActive = active;
        if (category is null)
        {
            AssignedText = ActivityText = AvailableText = string.Empty;
            Available = 0;
            Overspending = OverspendingKind.None;
            return;
        }

        AssignedText = Money(category.Assigned);
        ActivityText = Money(category.Activity);
        AvailableText = Money(category.Available);
        Overspending = category.Overspending;
        Available = category.Available.Amount;
    }

    /// <summary>Takes a group's sums of <paramref name="month"/>.</summary>
    public void Update(DateOnly month, bool active, BudgetGroupDto? group)
    {
        Month = month;
        IsActive = active;
        AssignedText = group is null ? string.Empty : Money(group.Assigned);
        ActivityText = group is null ? string.Empty : Money(group.Activity);
        AvailableText = group is null ? string.Empty : Money(group.Available);
        Available = group?.Available.Amount ?? 0;
        Overspending = OverspendingKind.None;
    }

    /// <summary>Shows a just-committed Assigned value until the recomputed months arrive.</summary>
    public void ShowPendingAssigned(long value, string currency) => AssignedText = LedgerText.Money(value, currency);

    /// <summary>The row's cursor or edit state changed.</summary>
    internal void NotifyCursor()
    {
        OnPropertyChanged(nameof(IsAssignedSelected));
        OnPropertyChanged(nameof(IsActivitySelected));
        OnPropertyChanged(nameof(IsAvailableSelected));
        OnPropertyChanged(nameof(IsEditing));
    }

    private static string Money(Money money) => LedgerText.Money(money.Amount, money.Currency);
}
