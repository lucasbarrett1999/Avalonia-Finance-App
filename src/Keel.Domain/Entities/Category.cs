namespace Keel.Domain.Entities;

/// <summary>A budget category (envelope).</summary>
public class Category
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Owning group.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display order within the group.</summary>
    public int SortOrder { get; set; }

    /// <summary>System categories (Ready to Assign, Credit Card Payment categories).</summary>
    public bool IsSystem { get; set; }

    /// <summary>Hidden categories are excluded from the budget view.</summary>
    public bool IsHidden { get; set; }

    /// <summary>For Credit Card Payment categories: the credit account they pay.</summary>
    public Guid? LinkedAccountId { get; set; }

    /// <summary>Free-text notes.</summary>
    public string? Notes { get; set; }

    /// <summary>Flex-mode tag.</summary>
    public FlexKind FlexKind { get; set; }

    /// <summary>Owning group.</summary>
    public CategoryGroup? Group { get; set; }
}
