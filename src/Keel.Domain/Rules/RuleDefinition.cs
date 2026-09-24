using Keel.Domain.Entities;

namespace Keel.Domain.Rules;

/// <summary>
/// A parsed rule (F-TXN-4): the <see cref="Rule"/> entity's columns with its conditions and
/// actions deserialized. Rules are evaluated by <see cref="SortOrder"/>; the first match wins
/// unless <see cref="ContinueAfterMatch"/> is set.
/// </summary>
public sealed record RuleDefinition
{
    /// <summary>Rule id.</summary>
    public Guid Id { get; init; }

    /// <summary>Display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Evaluation order (ascending).</summary>
    public int SortOrder { get; init; }

    /// <summary>Disabled rules are skipped.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Keep evaluating later rules after this one matches.</summary>
    public bool ContinueAfterMatch { get; init; }

    /// <summary>Conditions.</summary>
    public RuleConditionSet Conditions { get; init; } = new();

    /// <summary>Actions.</summary>
    public RuleActionSet Actions { get; init; } = new();

    /// <summary>Parses a stored rule. Throws <see cref="RuleFormatException"/> for invalid or newer JSON.</summary>
    public static RuleDefinition FromEntity(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new RuleDefinition
        {
            Id = rule.Id,
            Name = rule.Name,
            SortOrder = rule.SortOrder,
            IsEnabled = rule.IsEnabled,
            ContinueAfterMatch = rule.ContinueAfterMatch,
            Conditions = RuleJson.DeserializeConditions(rule.ConditionsJson),
            Actions = RuleJson.DeserializeActions(rule.ActionsJson),
        };
    }

    /// <summary>Creates the entity to store, with conditions and actions as versioned JSON.</summary>
    public Rule ToEntity() => new()
    {
        Id = Id == Guid.Empty ? EntityIds.New() : Id,
        Name = Name,
        SortOrder = SortOrder,
        IsEnabled = IsEnabled,
        ContinueAfterMatch = ContinueAfterMatch,
        ConditionsJson = RuleJson.Serialize(Conditions),
        ActionsJson = RuleJson.Serialize(Actions),
    };

    /// <summary>A one-line English summary: <c>If payee contains "x" and is an outflow: set category to Groceries</c>.</summary>
    public string Describe(RuleNames? names = null)
    {
        var joiner = Conditions.Match == RuleMatchMode.All ? " and " : " or ";
        var conditions = Conditions.Conditions.Count == 0
            ? "always"
            : string.Join(joiner, Conditions.Conditions.Select(c => c.Describe(names)));
        var actions = Actions.Actions.Count == 0
            ? "do nothing"
            : string.Join(", ", Actions.Actions.Select(a => a.Describe(names)));
        return $"If {conditions}: {actions}" + (ContinueAfterMatch ? " (then continue)" : string.Empty);
    }
}
