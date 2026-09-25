using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Budget;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>A month column header of the three-month grid: the month and its Ready to Assign (6.4.1).</summary>
public sealed partial class BudgetMonthHeaderViewModel : ObservableObject
{
    private readonly BudgetViewModel _page;

    /// <summary>Creates the header at <paramref name="offset"/> of the window.</summary>
    public BudgetMonthHeaderViewModel(BudgetViewModel page, int offset)
    {
        _page = page;
        Offset = offset;
    }

    /// <summary>Position in the window.</summary>
    public int Offset { get; }

    /// <summary>The month.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(AutomationName))]
    public partial DateOnly Month { get; private set; }

    /// <summary>"Sep 2026".</summary>
    public string Title => BudgetText.Month(Month);

    /// <summary>Ready to Assign of the month.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string ReadyToAssignText { get; private set; } = string.Empty;

    /// <summary>Negative Ready to Assign (red).</summary>
    [ObservableProperty]
    public partial bool IsNegative { get; private set; }

    /// <summary>The month the cursor is in.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>Screen-reader text of the header button.</summary>
    public string AutomationName => LedgerText.Format(Strings.BudgetMonths_HeaderAutomation, Title, ReadyToAssignText);

    /// <summary>Moves the cursor to this month and explains its Ready to Assign.</summary>
    [RelayCommand]
    public void Explain()
    {
        _page.SelectMonth(Offset);
        _page.ExplainReadyToAssign();
    }

    /// <summary>Takes the month's numbers.</summary>
    public void Update(DateOnly month, bool active, BudgetMonthDto? data)
    {
        Month = month;
        IsActive = active;
        ReadyToAssignText = data is null ? string.Empty : BudgetText.Money(data.ReadyToAssign);
        IsNegative = data is { ReadyToAssign.Amount: < 0 };
    }
}
