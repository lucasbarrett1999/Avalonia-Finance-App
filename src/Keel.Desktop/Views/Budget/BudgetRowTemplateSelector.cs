using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views.Budget;

/// <summary>
/// Picks the row template of the budget grid for the current mode: one month (four columns) or three
/// months side by side (ADR 0090). The view model re-adds the rows when the mode changes, so every
/// container is built again with the right template.
/// </summary>
public sealed class BudgetRowTemplateSelector : IDataTemplate
{
    /// <summary>Group row, one month.</summary>
    public IDataTemplate? Group { get; set; }

    /// <summary>Category row, one month.</summary>
    public IDataTemplate? Category { get; set; }

    /// <summary>Group row, three months.</summary>
    public IDataTemplate? GroupMonths { get; set; }

    /// <summary>Category row, three months.</summary>
    public IDataTemplate? CategoryMonths { get; set; }

    /// <summary>Whether the three-month templates are used (set by the view).</summary>
    public Func<bool>? ThreeMonths { get; set; }

    /// <inheritdoc />
    public Control? Build(object? param)
    {
        var three = ThreeMonths?.Invoke() ?? false;
        return param switch
        {
            BudgetGroupRowViewModel => (three ? GroupMonths : Group)?.Build(param),
            BudgetCategoryRowViewModel => (three ? CategoryMonths : Category)?.Build(param),
            _ => null,
        };
    }

    /// <inheritdoc />
    public bool Match(object? data) => data is BudgetRowViewModel;
}

/// <summary>Column sizes of the three-month grid (ADR 0090).</summary>
public static class BudgetGridSizes
{
    /// <summary>Width of one month (Assigned 92, Activity 92, Available 104).</summary>
    public const double MonthWidth = 288;

    /// <summary>Narrowest Name column before the grid scrolls sideways.</summary>
    public const double NameMinWidth = 160;

    /// <summary>Narrowest three-month table: the Name column, three months and the scroll bar gutter.</summary>
    public const double ThreeMonthMinWidth = NameMinWidth + (3 * MonthWidth) + 12;
}
