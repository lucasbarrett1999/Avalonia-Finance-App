using System.Text.RegularExpressions;
using Keel.Domain.Import;

namespace Keel.Domain.Rules;

/// <summary>Settings for a rule run.</summary>
public sealed record RuleEngineOptions
{
    /// <summary>Default options.</summary>
    public static RuleEngineOptions Default { get; } = new();

    /// <summary>Timeout for one regular-expression match.</summary>
    public TimeSpan RegexMatchTimeout { get; init; } = RuleRegex.DefaultMatchTimeout;

    /// <summary>Names used in trace descriptions.</summary>
    public RuleNames Names { get; init; } = RuleNames.Empty;
}

/// <summary>
/// Applies ordered rules to a transaction (F-TXN-4). Pure and deterministic: no I/O, no clock, no
/// shared state. Semantics (ADR 0020):
/// <list type="bullet">
/// <item>Rules run in ascending <see cref="RuleDefinition.SortOrder"/> (stable for ties, so input order breaks them).</item>
/// <item>Disabled rules and rules with validation errors are skipped and listed in the trace.</item>
/// <item>A matching rule's actions run in order on a working copy; later rules see the result.</item>
/// <item>The first matching rule stops evaluation unless it has <see cref="RuleDefinition.ContinueAfterMatch"/>.</item>
/// <item>A regular expression that times out counts as "no match" and is noted in the trace.</item>
/// </list>
/// </summary>
public static class RuleEngine
{
    /// <summary>Compiles and applies <paramref name="orderedRules"/> to one transaction.</summary>
    public static RuleApplication Apply(TransactionSnapshot snapshot, IEnumerable<RuleDefinition> orderedRules, RuleEngineOptions? options = null) =>
        Compile(orderedRules, options).Apply(snapshot);

    /// <summary>
    /// Validates the rules and compiles their regular expressions once, for applying to many
    /// transactions (import batches, retroactive preview).
    /// </summary>
    public static CompiledRuleSet Compile(IEnumerable<RuleDefinition> rules, RuleEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new CompiledRuleSet(rules, options ?? RuleEngineOptions.Default);
    }
}

/// <summary>Rules prepared by <see cref="RuleEngine.Compile"/>. Immutable and thread-safe.</summary>
public sealed class CompiledRuleSet
{
    private readonly CompiledRule[] _rules;
    private readonly RuleEngineOptions _options;

    internal CompiledRuleSet(IEnumerable<RuleDefinition> rules, RuleEngineOptions options)
    {
        _options = options;
        _rules = rules.OrderBy(r => r.SortOrder).Select(r => new CompiledRule(r, options)).ToArray();
        Rules = _rules.Select(r => r.Definition).ToArray();
    }

    /// <summary>The rules in evaluation order.</summary>
    public IReadOnlyList<RuleDefinition> Rules { get; }

    /// <summary>Applies the rules to one transaction.</summary>
    public RuleApplication Apply(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var working = snapshot;
        var evaluations = new List<RuleEvaluation>();
        var setBy = new Dictionary<RuleChanges, Guid>();
        var normalizedCache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rule in _rules)
        {
            var definition = rule.Definition;
            if (!definition.IsEnabled)
            {
                evaluations.Add(new RuleEvaluation(definition.Id, definition.Name, RuleOutcome.Disabled, [], [], [], false));
                continue;
            }

            if (rule.Errors.Count > 0)
            {
                evaluations.Add(new RuleEvaluation(definition.Id, definition.Name, RuleOutcome.Invalid, [], [], rule.Errors, false));
                continue;
            }

            var conditionResults = rule.Evaluate(working, normalizedCache);
            var matched = definition.Conditions.Match == RuleMatchMode.All
                ? conditionResults.All(c => c.Matched)
                : conditionResults.Any(c => c.Matched);
            if (!matched)
            {
                evaluations.Add(new RuleEvaluation(definition.Id, definition.Name, RuleOutcome.NotMatched, conditionResults, [], [], false));
                continue;
            }

            var actionResults = new List<ActionResult>(definition.Actions.Actions.Count);
            for (var i = 0; i < definition.Actions.Actions.Count; i++)
            {
                var action = definition.Actions.Actions[i];
                var (next, note) = ActionApplier.Apply(action, working);
                var applied = note is null && !ReferenceEquals(next, working);
                if (applied)
                {
                    foreach (var field in ActionApplier.Fields(working, next))
                    {
                        setBy[field] = definition.Id;
                    }

                    working = next;
                }

                actionResults.Add(new ActionResult(i, action.Describe(_options.Names), applied, applied ? null : note ?? "no change"));
            }

            var stop = !definition.ContinueAfterMatch;
            evaluations.Add(new RuleEvaluation(definition.Id, definition.Name, RuleOutcome.Matched, conditionResults, actionResults, [], stop));
            if (stop)
            {
                break;
            }
        }

        var changes = ActionApplier.Fields(snapshot, working).Aggregate(RuleChanges.None, (acc, f) => acc | f);
        var added = working.Tags.Where(t => !snapshot.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
        var finalSetBy = setBy.Where(kv => (changes & kv.Key) != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        var mutations = new RuleMutations { Result = working, Changes = changes, AddedTags = added, SetBy = finalSetBy };
        return new RuleApplication(snapshot, mutations, new RuleTrace(evaluations));
    }

    /// <summary>Applies the rules to each transaction (e.g. a retroactive preview).</summary>
    public IReadOnlyList<RuleApplication> ApplyAll(IEnumerable<TransactionSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots.Select(Apply).ToArray();
    }

    private sealed class CompiledRule
    {
        private readonly Func<TransactionSnapshot, Dictionary<string, string>, ConditionResult>[] _conditions;

        public CompiledRule(RuleDefinition definition, RuleEngineOptions options)
        {
            Definition = definition;
            Errors = RuleValidator.Validate(definition)
                .Where(p => p.Severity == RuleProblemSeverity.Error)
                .Select(p => p.Message)
                .ToArray();
            _conditions = Errors.Count > 0
                ? []
                : definition.Conditions.Conditions.Select((c, i) => ConditionCompiler.Compile(c, i, options)).ToArray();
        }

        public RuleDefinition Definition { get; }

        public IReadOnlyList<string> Errors { get; }

        public IReadOnlyList<ConditionResult> Evaluate(TransactionSnapshot snapshot, Dictionary<string, string> normalizedCache)
        {
            var results = new ConditionResult[_conditions.Length];
            for (var i = 0; i < _conditions.Length; i++)
            {
                results[i] = _conditions[i](snapshot, normalizedCache);
            }

            return results;
        }
    }
}

/// <summary>Turns conditions into evaluators (regular expressions compiled once).</summary>
internal static class ConditionCompiler
{
    public static Func<TransactionSnapshot, Dictionary<string, string>, ConditionResult> Compile(RuleCondition condition, int index, RuleEngineOptions options)
    {
        var description = condition.Describe(options.Names);
        ConditionResult Result(bool matched, string? note = null) => new(index, description, matched, note);

        switch (condition)
        {
            case PayeeCondition payee:
                {
                    var matcher = TextMatcher.Create(payee.Operator, payee.Value, payee.Normalized, options.RegexMatchTimeout);
                    return (s, cache) =>
                    {
                        string? note = null;
                        foreach (var text in PayeeTexts(s, payee.Normalized, cache))
                        {
                            var (ok, timedOut) = matcher.Match(text);
                            if (ok)
                            {
                                return Result(true);
                            }

                            note ??= timedOut ? "regular expression timed out" : null;
                        }

                        return Result(false, note);
                    };
                }

            case MemoCondition memo:
                {
                    var matcher = TextMatcher.Create(memo.Operator, memo.Value, normalized: false, options.RegexMatchTimeout);
                    return (s, _) =>
                    {
                        var (ok, timedOut) = matcher.Match(s.Memo ?? string.Empty);
                        return Result(ok, timedOut ? "regular expression timed out" : null);
                    };
                }

            case AmountCondition amount:
                return (s, _) =>
                {
                    var value = amount.Signed ? s.Amount : (s.Amount == long.MinValue ? long.MaxValue : Math.Abs(s.Amount));
                    var ok = amount.Operator switch
                    {
                        AmountOperator.EqualTo => value == amount.Amount,
                        AmountOperator.Between => value >= amount.Amount && value <= (amount.AmountMax ?? amount.Amount),
                        AmountOperator.GreaterThan => value > amount.Amount,
                        AmountOperator.LessThan => value < amount.Amount,
                        _ => false,
                    };
                    return Result(ok);
                };

            case DirectionCondition direction:
                return (s, _) => Result(direction.Direction == TransactionDirection.Inflow ? s.Amount > 0 : s.Amount < 0);

            case AccountCondition account:
                {
                    var set = account.AccountIds.ToHashSet();
                    return (s, _) => Result(set.Contains(s.AccountId));
                }

            case SourceCondition source:
                {
                    var set = source.Sources.ToHashSet();
                    return (s, _) => Result(set.Contains(s.Source));
                }

            case DateRangeCondition range:
                return (s, _) => Result((range.From is not { } from || s.Date >= from) && (range.To is not { } to || s.Date <= to));

            case TagCondition tag:
                {
                    var wanted = tag.Tag.Trim();
                    return (s, _) => Result(s.Tags.Any(t => string.Equals(t.Trim(), wanted, StringComparison.OrdinalIgnoreCase)));
                }

            default:
                return (_, _) => Result(false, "unknown condition");
        }
    }

    private static IEnumerable<string> PayeeTexts(TransactionSnapshot s, bool normalized, Dictionary<string, string> cache)
    {
        var first = s.Payee ?? string.Empty;
        var second = s.PayeeRaw ?? string.Empty;
        yield return Prepare(first, normalized, cache);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            yield return Prepare(second, normalized, cache);
        }
    }

    private static string Prepare(string text, bool normalized, Dictionary<string, string> cache)
    {
        if (!normalized)
        {
            return text;
        }

        if (!cache.TryGetValue(text, out var result))
        {
            result = PayeeNormalizer.Normalize(text);
            cache[text] = result;
        }

        return result;
    }

    private sealed class TextMatcher
    {
        private readonly TextOperator _op;
        private readonly string _value;
        private readonly Regex? _regex;

        private TextMatcher(TextOperator op, string value, Regex? regex)
        {
            _op = op;
            _value = value;
            _regex = regex;
        }

        public static TextMatcher Create(TextOperator op, string value, bool normalized, TimeSpan timeout)
        {
            if (op == TextOperator.Regex)
            {
                // The validator has already rejected invalid patterns.
                return new TextMatcher(op, value, RuleRegex.TryCreate(value, timeout, out _));
            }

            var prepared = normalized ? PayeeNormalizer.Normalize(value) : value.Trim();
            return new TextMatcher(op, prepared, null);
        }

        public (bool Matched, bool TimedOut) Match(string text)
        {
            switch (_op)
            {
                case TextOperator.Contains:
                    return (text.Contains(_value, StringComparison.OrdinalIgnoreCase), false);
                case TextOperator.EqualTo:
                    return (string.Equals(text.Trim(), _value, StringComparison.OrdinalIgnoreCase), false);
                case TextOperator.StartsWith:
                    return (text.TrimStart().StartsWith(_value, StringComparison.OrdinalIgnoreCase), false);
                case TextOperator.Regex when _regex is not null:
                    try
                    {
                        return (_regex.IsMatch(text), false);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return (false, true);
                    }

                default:
                    return (false, false);
            }
        }
    }
}

/// <summary>Applies one action to a snapshot.</summary>
internal static class ActionApplier
{
    /// <summary>Returns the new snapshot (the same instance when nothing changed) and a note when the action was skipped.</summary>
    public static (TransactionSnapshot Next, string? Note) Apply(RuleAction action, TransactionSnapshot s)
    {
        switch (action)
        {
            case SetPayeeAction setPayee:
                {
                    var payee = setPayee.Payee.Trim();
                    return string.Equals(s.Payee, payee, StringComparison.Ordinal)
                        ? (s, "payee is already \"" + payee + "\"")
                        : (s with { Payee = payee }, null);
                }

            case SetCategoryAction setCategory:
                return s.CategoryId == setCategory.CategoryId && !s.IsSplit
                    ? (s, "category is already set")
                    : (s with { CategoryId = setCategory.CategoryId, Splits = [] }, null);

            case SetMemoAction setMemo:
                {
                    var memo = setMemo.Memo.Length == 0 ? null : setMemo.Memo;
                    return string.Equals(s.Memo, memo, StringComparison.Ordinal)
                        ? (s, "memo is already set")
                        : (s with { Memo = memo }, null);
                }

            case AppendMemoAction append:
                {
                    var current = s.Memo ?? string.Empty;
                    if (current.EndsWith(append.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        return (s, "memo already ends with \"" + append.Text + "\"");
                    }

                    var memo = current.Length == 0 ? append.Text : current.TrimEnd() + " " + append.Text;
                    return (s with { Memo = memo }, null);
                }

            case AddTagAction addTag:
                {
                    var tag = addTag.Tag.Trim();
                    return s.Tags.Any(t => string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase))
                        ? (s, "already tagged \"" + tag + "\"")
                        : (s with { Tags = [.. s.Tags, tag] }, null);
                }

            case MarkApprovedAction:
                return s.IsApproved ? (s, "already approved") : (s with { IsApproved = true }, null);

            case FlagAction:
                return s.IsFlagged ? (s, "already flagged") : (s with { IsFlagged = true }, null);

            case SplitByAmountsAction byAmounts:
                return SplitByAmounts(byAmounts, s);

            case SplitByPercentagesAction byPercent:
                return SplitByPercentages(byPercent, s);

            case SetTransferAccountAction transfer:
                if (transfer.AccountId == s.AccountId)
                {
                    return (s, "a transaction cannot be a transfer to its own account");
                }

                return s.TransferAccountId == transfer.AccountId
                    ? (s, "already a transfer with that account")
                    : (s with { TransferAccountId = transfer.AccountId }, null);

            default:
                return (s, "unknown action");
        }
    }

    /// <summary>The fields that differ between two snapshots.</summary>
    public static IEnumerable<RuleChanges> Fields(TransactionSnapshot before, TransactionSnapshot after)
    {
        if (!string.Equals(before.Payee, after.Payee, StringComparison.Ordinal))
        {
            yield return RuleChanges.Payee;
        }

        if (before.CategoryId != after.CategoryId)
        {
            yield return RuleChanges.Category;
        }

        if (!string.Equals(before.Memo, after.Memo, StringComparison.Ordinal))
        {
            yield return RuleChanges.Memo;
        }

        if (!before.Tags.SequenceEqual(after.Tags, StringComparer.Ordinal))
        {
            yield return RuleChanges.Tags;
        }

        if (before.IsApproved != after.IsApproved)
        {
            yield return RuleChanges.Approved;
        }

        if (before.IsFlagged != after.IsFlagged)
        {
            yield return RuleChanges.Flagged;
        }

        if (!before.Splits.SequenceEqual(after.Splits))
        {
            yield return RuleChanges.Splits;
        }

        if (before.TransferAccountId != after.TransferAccountId)
        {
            yield return RuleChanges.TransferAccount;
        }
    }

    private static (TransactionSnapshot, string?) SplitByAmounts(SplitByAmountsAction action, TransactionSnapshot s)
    {
        if (s.Amount == 0)
        {
            return (s, "a zero amount cannot be split");
        }

        var sign = Math.Sign(s.Amount);
        var magnitude = s.Amount == long.MinValue ? long.MaxValue : Math.Abs(s.Amount);
        long fixedTotal = 0;
        foreach (var line in action.Lines.Take(action.Lines.Count - 1))
        {
            fixedTotal = checked(fixedTotal + (line.Amount ?? 0));
        }

        var remainder = magnitude - fixedTotal;
        if (remainder <= 0)
        {
            return (s, "the fixed amounts leave nothing for the last line");
        }

        var splits = new SnapshotSplit[action.Lines.Count];
        for (var i = 0; i < action.Lines.Count; i++)
        {
            var line = action.Lines[i];
            var amount = i == action.Lines.Count - 1 ? remainder : line.Amount ?? 0;
            splits[i] = new SnapshotSplit(line.CategoryId, sign * amount, line.Memo);
        }

        return (s with { CategoryId = null, Splits = splits }, null);
    }

    private static (TransactionSnapshot, string?) SplitByPercentages(SplitByPercentagesAction action, TransactionSnapshot s)
    {
        if (s.Amount == 0)
        {
            return (s, "a zero amount cannot be split");
        }

        var parts = new Money(s.Amount, s.Currency).SplitByPercentages(action.Lines.Select(l => l.Percent).ToArray());
        var splits = action.Lines
            .Select((line, i) => new SnapshotSplit(line.CategoryId, parts[i].Amount, line.Memo))
            .ToArray();
        return (s with { CategoryId = null, Splits = splits }, null);
    }
}
