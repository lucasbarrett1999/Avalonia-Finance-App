using Keel.Domain;

namespace Keel.Application.Categories;

/// <summary>Category lookup for pickers and category management (F-BUD-1). Every change is one undoable ledger action.</summary>
public interface ICategoryService
{
    /// <summary>Categories in group and category order.</summary>
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(bool includeHidden, CancellationToken ct);

    /// <summary>Creates a category in the named group (creating the group if needed).</summary>
    Task<CategoryDto> CreateCategoryAsync(string groupName, string name, CancellationToken ct);

    /// <summary>
    /// Every group except the system Inflow group, with all its categories (hidden ones included),
    /// in display order: the model of the "Manage categories" dialog (F-BUD-1).
    /// </summary>
    Task<IReadOnlyList<CategoryGroupDto>> GetGroupsAsync(CancellationToken ct);

    /// <summary>Creates a user group at the end of the group order.</summary>
    Task<CategoryGroupDto> CreateGroupAsync(string name, CancellationToken ct);

    /// <summary>Creates a category at the end of a user group.</summary>
    Task<CategoryDto> CreateCategoryAsync(Guid groupId, string name, CancellationToken ct);

    /// <summary>Renames a user group.</summary>
    Task RenameGroupAsync(Guid groupId, string name, CancellationToken ct);

    /// <summary>Hides or shows a user group (hidden groups leave the budget view; their money still counts).</summary>
    Task SetGroupHiddenAsync(Guid groupId, bool hidden, CancellationToken ct);

    /// <summary>Sets the group order; <paramref name="order"/> lists every group returned by <see cref="GetGroupsAsync"/>.</summary>
    Task ReorderGroupsAsync(IReadOnlyList<Guid> order, CancellationToken ct);

    /// <summary>
    /// Deletes a user group and its categories. When any of them has history,
    /// <paramref name="replacementCategoryId"/> (a category outside the group) receives it, as in
    /// <see cref="DeleteCategoryAsync"/>.
    /// </summary>
    Task DeleteGroupAsync(Guid groupId, Guid? replacementCategoryId, CancellationToken ct);

    /// <summary>Renames a user category.</summary>
    Task RenameCategoryAsync(Guid categoryId, string name, CancellationToken ct);

    /// <summary>Hides or shows a user category.</summary>
    Task SetCategoryHiddenAsync(Guid categoryId, bool hidden, CancellationToken ct);

    /// <summary>Moves a user category to position <paramref name="index"/> of a user group (the same or another one).</summary>
    Task MoveCategoryAsync(Guid categoryId, Guid groupId, int index, CancellationToken ct);

    /// <summary>What refers to a category (decides whether deleting it needs a replacement).</summary>
    Task<CategoryUsageDto> GetUsageAsync(Guid categoryId, CancellationToken ct);

    /// <summary>
    /// Deletes a user category (F-BUD-1). With history, <paramref name="replacementCategoryId"/> is
    /// required: its transactions, splits, schedules and recurring items are re-categorized to the
    /// replacement, its assignments are added to the replacement's in the same months, and payee
    /// defaults follow. Its target is removed. One undoable action.
    /// </summary>
    Task DeleteCategoryAsync(Guid categoryId, Guid? replacementCategoryId, CancellationToken ct);

    /// <summary>The free-text note of a category (F-BUD-7), or null.</summary>
    Task<string?> GetNoteAsync(Guid categoryId, CancellationToken ct);

    /// <summary>Sets or clears the note of a category (F-BUD-7).</summary>
    Task SetNoteAsync(Guid categoryId, string? note, CancellationToken ct);

    /// <summary>
    /// Applies a starter template (F-BUD-8): creates the groups and categories that do not exist
    /// yet (matching names case-insensitively) and never deletes. Returns the number of categories created.
    /// </summary>
    Task<int> ApplyTemplateAsync(IReadOnlyList<CategoryTemplateGroup> groups, CancellationToken ct);

    /// <summary>
    /// Tags a regular category Fixed, Non-monthly or Flex for the Flex view (F-BUD-6), or back to automatic
    /// with <see cref="FlexKind.Unset"/>. One undoable action; no effect on budget numbers.
    /// </summary>
    Task SetFlexKindAsync(Guid categoryId, FlexKind kind, CancellationToken ct);
}

/// <summary>A category group with its categories (category management).</summary>
/// <param name="Id">Group id.</param>
/// <param name="Name">Name.</param>
/// <param name="IsSystem">System group (Credit Card Payments): cannot be renamed, hidden or deleted.</param>
/// <param name="IsHidden">Hidden group.</param>
/// <param name="Categories">Categories in display order, hidden ones included.</param>
public sealed record CategoryGroupDto(Guid Id, string Name, bool IsSystem, bool IsHidden, IReadOnlyList<CategoryDto> Categories);

/// <summary>What refers to a category.</summary>
/// <param name="Transactions">Transactions and split lines (soft-deleted ones included).</param>
/// <param name="Assignments">Months with money assigned.</param>
/// <param name="Scheduled">Scheduled transactions.</param>
/// <param name="HasTarget">Whether the category has a target (removed on delete).</param>
public sealed record CategoryUsageDto(int Transactions, int Assignments, int Scheduled, bool HasTarget)
{
    /// <summary>Whether deleting needs a replacement category.</summary>
    public bool HasHistory => Transactions + Assignments + Scheduled > 0;
}

/// <summary>A group of a starter template (F-BUD-8).</summary>
/// <param name="Name">Group name.</param>
/// <param name="Categories">Category names.</param>
public sealed record CategoryTemplateGroup(string Name, IReadOnlyList<string> Categories);

/// <summary>A category with its group (<c>FlexKind</c>: the Flex-mode tag, Unset for automatic).</summary>
public sealed record CategoryDto(
    Guid Id,
    Guid GroupId,
    string GroupName,
    string Name,
    bool IsSystem,
    bool IsHidden,
    Guid? LinkedAccountId,
    FlexKind FlexKind = FlexKind.Unset)
{
    /// <summary>Whether this is a Credit Card Payment category (not assignable to spending).</summary>
    public bool IsCreditCardPayment => LinkedAccountId is not null;

    /// <summary>"Group: Name" for pickers and search.</summary>
    public string FullName => GroupName + ": " + Name;
}
