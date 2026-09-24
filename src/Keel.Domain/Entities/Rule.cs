namespace Keel.Domain.Entities;

/// <summary>An ordered categorization/rename rule (F-TXN-4). Conditions and actions are versioned JSON.</summary>
public class Rule
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Evaluation order.</summary>
    public int SortOrder { get; set; }

    /// <summary>Disabled rules are skipped.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Keep evaluating later rules after this one matches.</summary>
    public bool ContinueAfterMatch { get; set; }

    /// <summary>Conditions as versioned JSON.</summary>
    public string ConditionsJson { get; set; } = "{}";

    /// <summary>Actions as versioned JSON.</summary>
    public string ActionsJson { get; set; } = "{}";
}
