namespace Keel.Application.Categories;

/// <summary>Category lookup for pickers (the budget screen that manages categories is M2).</summary>
public interface ICategoryService
{
    /// <summary>Categories in group and category order.</summary>
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(bool includeHidden, CancellationToken ct);

    /// <summary>Creates a category in the named group (creating the group if needed).</summary>
    Task<CategoryDto> CreateCategoryAsync(string groupName, string name, CancellationToken ct);
}

/// <summary>A category with its group.</summary>
public sealed record CategoryDto(
    Guid Id,
    Guid GroupId,
    string GroupName,
    string Name,
    bool IsSystem,
    bool IsHidden,
    Guid? LinkedAccountId)
{
    /// <summary>Whether this is a Credit Card Payment category (not assignable to spending).</summary>
    public bool IsCreditCardPayment => LinkedAccountId is not null;

    /// <summary>"Group: Name" for pickers and search.</summary>
    public string FullName => GroupName + ": " + Name;
}
