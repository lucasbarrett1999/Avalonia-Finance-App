namespace Keel.Domain.Entities;

/// <summary>A payee. <see cref="NormalizedName"/> is unique.</summary>
public class Payee
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized name used for matching; unique.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Category pre-filled when this payee is chosen.</summary>
    public Guid? DefaultCategoryId { get; set; }

    /// <summary>For the built-in "Transfer : Account" payees, the account.</summary>
    public Guid? IsTransferPayeeForAccountId { get; set; }
}
