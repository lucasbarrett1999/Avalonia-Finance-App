using CommunityToolkit.Mvvm.ComponentModel;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>One editable split line in the transaction editor (F-ACC-5).</summary>
public sealed partial class SplitLineViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates a line; <paramref name="changed"/> runs when the amount or category changes.</summary>
    public SplitLineViewModel(IReadOnlyList<CategoryOption> categories, Action changed)
    {
        Categories = categories;
        _changed = changed;
    }

    /// <summary>Categories to pick from.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Selected category.</summary>
    [ObservableProperty]
    public partial CategoryOption? Category { get; set; }

    /// <summary>Category text as typed.</summary>
    [ObservableProperty]
    public partial string? CategoryText { get; set; }

    /// <summary>Memo.</summary>
    [ObservableProperty]
    public partial string? Memo { get; set; }

    /// <summary>Outflow in minor units (positive).</summary>
    [ObservableProperty]
    public partial long Outflow { get; set; }

    /// <summary>Inflow in minor units (positive).</summary>
    [ObservableProperty]
    public partial long Inflow { get; set; }

    /// <summary>Signed amount.</summary>
    public long Amount => Inflow - Outflow;

    /// <summary>Sets the amount from a signed value.</summary>
    public void SetAmount(long amount)
    {
        Outflow = amount < 0 ? -amount : 0;
        Inflow = amount > 0 ? amount : 0;
    }

    /// <summary>The category chosen or typed (exact or unique match on the text).</summary>
    public CategoryOption? ResolveCategory() => TransactionEditorViewModel.ResolveCategory(Categories, Category, CategoryText);

    partial void OnOutflowChanged(long value) => _changed();

    partial void OnInflowChanged(long value) => _changed();
}
