using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Desktop.Resources;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>A category in pickers and the filter bar; a null id is "All categories".</summary>
public sealed record CategoryOption(Guid? Id, string Name, string Group)
{
    /// <summary>"Group: Name", used for display and search-as-you-type.</summary>
    public string FullName => Id is null ? Name : Group + ": " + Name;

    /// <summary>The "All categories" filter entry.</summary>
    public static CategoryOption All { get; } = new(null, Strings.Register_FilterAllCategories, string.Empty);

    /// <summary>Creates an option from a category.</summary>
    public static CategoryOption From(CategoryDto category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new(category.Id, category.Name, category.GroupName);
    }

    /// <inheritdoc />
    public override string ToString() => FullName;
}

/// <summary>An account in pickers (transfer targets, All Accounts entry, move).</summary>
public sealed record AccountOption(Guid Id, string Name, bool IsOnBudget, bool IsClosed, string Currency)
{
    /// <summary>Creates an option from an account.</summary>
    public static AccountOption From(AccountDto account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new(account.Id, account.Name, account.IsOnBudget, account.IsClosed, account.Balance.Currency);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A labelled enum value for combo boxes.</summary>
/// <typeparam name="T">Enum type.</typeparam>
public sealed record Choice<T>(T Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Date-range presets of the filter bar (PRD 9.4).</summary>
public enum DatePreset
{
    /// <summary>No date limit.</summary>
    AllDates,

    /// <summary>The current calendar month.</summary>
    ThisMonth,

    /// <summary>The previous calendar month.</summary>
    LastMonth,

    /// <summary>The last three calendar months including this one.</summary>
    LastThreeMonths,

    /// <summary>The current calendar year.</summary>
    ThisYear,

    /// <summary>The previous calendar year.</summary>
    LastYear,
}

/// <summary>Status filter of the filter bar.</summary>
public enum StatusFilter
{
    /// <summary>All statuses.</summary>
    All,

    /// <summary>Only uncleared.</summary>
    Uncleared,

    /// <summary>Only cleared (not reconciled).</summary>
    Cleared,

    /// <summary>Only reconciled.</summary>
    Reconciled,

    /// <summary>Uncleared and cleared (everything not yet reconciled).</summary>
    NotReconciled,
}
