using Keel.Domain.Rules;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Rules;

/// <summary>
/// Rewrites the names rules refer to when a tag or payee is renamed or merged (F-TXN-8, F-TXN-9, ADR 0097).
/// Rules name tags and payees by text in their JSON, so the tracked rule rows are rewritten inside the caller's
/// ledger action (audited and undone with it). Unreadable (newer-format) rules are left alone.
/// </summary>
internal static class RuleRewriter
{
    /// <summary>
    /// Applies <paramref name="condition"/> and <paramref name="action"/> to every condition and action of every
    /// rule and stores the rules that changed; returns how many changed.
    /// </summary>
    public static async Task<int> RewriteAsync(KeelDbContext db, Func<RuleCondition, RuleCondition> condition, Func<RuleAction, RuleAction> action, CancellationToken ct)
    {
        var changed = 0;
        foreach (var rule in await db.Rules.ToListAsync(ct).ConfigureAwait(false))
        {
            if (TryRead(rule) is not { } definition)
            {
                continue;
            }

            var conditions = definition.Conditions.Conditions.Select(condition).ToList();
            var actions = definition.Actions.Actions.Select(action).ToList();
            var conditionsChanged = !conditions.SequenceEqual(definition.Conditions.Conditions);
            var actionsChanged = !actions.SequenceEqual(definition.Actions.Actions);
            if (conditionsChanged)
            {
                rule.ConditionsJson = RuleJson.Serialize(definition.Conditions with { Conditions = conditions });
            }

            if (actionsChanged)
            {
                rule.ActionsJson = RuleJson.Serialize(definition.Actions with { Actions = actions });
            }

            changed += conditionsChanged || actionsChanged ? 1 : 0;
        }

        return changed;
    }

    /// <summary>How many readable rules have a condition or action matching the predicates.</summary>
    public static async Task<int> CountAsync(KeelDbContext db, Func<RuleCondition, bool> condition, Func<RuleAction, bool> action, CancellationToken ct)
    {
        var rules = await db.Rules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rules.Select(TryRead).Count(d => d is not null && (d.Conditions.Conditions.Any(condition) || d.Actions.Actions.Any(action)));
    }

    /// <summary>Every readable rule.</summary>
    public static async Task<IReadOnlyList<RuleDefinition>> ReadAllAsync(KeelDbContext db, CancellationToken ct)
    {
        var rules = await db.Rules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rules.Select(TryRead).OfType<RuleDefinition>().ToList();
    }

    private static RuleDefinition? TryRead(Domain.Entities.Rule rule)
    {
        try
        {
            return RuleDefinition.FromEntity(rule);
        }
        catch (RuleFormatException)
        {
            return null;
        }
    }
}
