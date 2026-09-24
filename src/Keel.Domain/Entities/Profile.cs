namespace Keel.Domain.Entities;

/// <summary>A person who owns accounts. v1 has one default row (household features are P2).</summary>
public class Profile
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this is the default profile.</summary>
    public bool IsDefault { get; set; }
}
