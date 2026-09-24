namespace Keel.Domain.Entities;

/// <summary>A group of budget categories (F-BUD-1).</summary>
public class CategoryGroup
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display order.</summary>
    public int SortOrder { get; set; }

    /// <summary>System groups (Inflow, Credit Card Payments) cannot be renamed or deleted.</summary>
    public bool IsSystem { get; set; }

    /// <summary>Hidden groups are excluded from the budget view.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Categories in this group.</summary>
    public ICollection<Category> Categories { get; } = new List<Category>();
}
