using System.Globalization;
using Keel.Domain.Import;

namespace Keel.Domain.Rules;

/// <summary>
/// Builds the prefilled rule for "Create rule from this transaction" (F-TXN-4). The user edits it
/// in the rule editor before saving; nothing here is persisted.
/// </summary>
public static class RuleSuggester
{
    /// <summary>
    /// Suggests a rule that reproduces how <paramref name="snapshot"/> is categorized:
    /// <list type="bullet">
    /// <item>Condition: the normalized raw descriptor equals the transaction's (robust to store
    /// numbers and card-processor noise); when normalization leaves nothing distinctive, "payee
    /// is" the raw text instead.</item>
    /// <item>Condition: the same direction (inflow or outflow).</item>
    /// <item>Actions: rename the payee when its name differs from the descriptor; set the category,
    /// or reproduce the splits as fixed amounts (last line takes the rest); make it a transfer when
    /// it is one; add its tags.</item>
    /// </list>
    /// The rule is enabled, stops at the first match, and is not auto-approving.
    /// </summary>
    /// <param name="snapshot">The selected transaction.</param>
    /// <param name="names">Names for the rule's name (category).</param>
    public static RuleDefinition FromTransaction(TransactionSnapshot snapshot, RuleNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var raw = string.IsNullOrWhiteSpace(snapshot.PayeeRaw) ? snapshot.Payee : snapshot.PayeeRaw;
        var normalized = PayeeNormalizer.Normalize(raw);

        var conditions = new List<RuleCondition>();
        if (normalized.Length > 0)
        {
            conditions.Add(new PayeeCondition(TextOperator.EqualTo, normalized, Normalized: true));
        }
        else if (!string.IsNullOrWhiteSpace(raw))
        {
            conditions.Add(new PayeeCondition(TextOperator.EqualTo, raw.Trim()));
        }

        if (snapshot.Amount != 0)
        {
            conditions.Add(new DirectionCondition(snapshot.Amount < 0 ? TransactionDirection.Outflow : TransactionDirection.Inflow));
        }

        var actions = new List<RuleAction>();
        if (!string.IsNullOrWhiteSpace(snapshot.Payee)
            && !string.Equals(snapshot.Payee.Trim(), raw.Trim(), StringComparison.Ordinal))
        {
            actions.Add(new SetPayeeAction(snapshot.Payee.Trim()));
        }

        if (snapshot.TransferAccountId is { } transfer)
        {
            actions.Add(new SetTransferAccountAction(transfer));
        }

        if (snapshot.IsSplit && snapshot.Splits.Count >= 2)
        {
            var lines = snapshot.Splits
                .Select((split, i) => new AmountSplitLine(
                    split.CategoryId,
                    i == snapshot.Splits.Count - 1 ? null : Math.Abs(split.Amount),
                    split.Memo))
                .ToArray();
            actions.Add(new SplitByAmountsAction(lines));
        }
        else if (snapshot.CategoryId is { } category)
        {
            actions.Add(new SetCategoryAction(category));
        }

        foreach (var tag in snapshot.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            actions.Add(new AddTagAction(tag.Trim()));
        }

        return new RuleDefinition
        {
            Id = Guid.Empty,
            Name = SuggestName(snapshot, normalized, raw, names),
            IsEnabled = true,
            ContinueAfterMatch = false,
            Conditions = new RuleConditionSet { Match = RuleMatchMode.All, Conditions = conditions },
            Actions = new RuleActionSet { Actions = actions },
        };
    }

    private static string SuggestName(TransactionSnapshot snapshot, string normalized, string raw, RuleNames? names)
    {
        var payee = !string.IsNullOrWhiteSpace(snapshot.Payee) ? snapshot.Payee.Trim()
            : normalized.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant())
            : raw.Trim();
        if (payee.Length == 0)
        {
            payee = "Transaction";
        }

        return snapshot.CategoryId is { } category && names is not null
            ? $"{payee} → {names.Category(category)}"
            : payee;
    }
}
