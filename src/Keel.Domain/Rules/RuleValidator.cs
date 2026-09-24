using System.Globalization;
using System.Text.RegularExpressions;

namespace Keel.Domain.Rules;

/// <summary>How serious a <see cref="RuleProblem"/> is.</summary>
public enum RuleProblemSeverity
{
    /// <summary>The rule cannot run; the engine skips it.</summary>
    Error,

    /// <summary>The rule runs, but probably not as intended.</summary>
    Warning,
}

/// <summary>Machine-readable kind of a <see cref="RuleProblem"/> (for UI localization and tests).</summary>
public enum RuleProblemCode
{
    /// <summary>The rule has no name.</summary>
    NameMissing,

    /// <summary>The rule has no conditions.</summary>
    NoConditions,

    /// <summary>The rule has no actions.</summary>
    NoActions,

    /// <summary>A text condition has no text.</summary>
    EmptyText,

    /// <summary>A regular expression does not parse.</summary>
    InvalidRegex,

    /// <summary>A regular expression is longer than <see cref="RuleRegex.MaxPatternLength"/>.</summary>
    RegexTooLong,

    /// <summary>An unsigned amount is negative.</summary>
    NegativeAmount,

    /// <summary>A "between" condition has no upper bound.</summary>
    MissingUpperBound,

    /// <summary>A "between" condition's lower bound is above its upper bound.</summary>
    ReversedBounds,

    /// <summary>An upper bound is set on an operator that ignores it.</summary>
    UnusedUpperBound,

    /// <summary>An account or source condition lists nothing.</summary>
    EmptySet,

    /// <summary>A date range has neither bound.</summary>
    OpenDateRange,

    /// <summary>A date range ends before it starts.</summary>
    ReversedDateRange,

    /// <summary>A tag condition or action has no tag name.</summary>
    EmptyTag,

    /// <summary>A set-payee action has no payee.</summary>
    EmptyPayee,

    /// <summary>A category or account id is empty.</summary>
    EmptyId,

    /// <summary>A category id is not a known category.</summary>
    UnknownCategory,

    /// <summary>An account id is not a known account.</summary>
    UnknownAccount,

    /// <summary>An append-memo action has no text.</summary>
    EmptyAppendText,

    /// <summary>A split has fewer than two lines.</summary>
    TooFewSplitLines,

    /// <summary>A fixed-amount split line (other than the last) has no amount or a non-positive one.</summary>
    InvalidSplitAmount,

    /// <summary>The last fixed-amount split line has an amount (it always receives the remainder).</summary>
    LastSplitLineHasAmount,

    /// <summary>A split percentage is not greater than 0.</summary>
    InvalidSplitPercent,

    /// <summary>Split percentages do not sum to 100.</summary>
    PercentagesNotHundred,

    /// <summary>Two actions set the same thing; only the last one has an effect.</summary>
    ConflictingActions,
}

/// <summary>A problem found by <see cref="RuleValidator"/>.</summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Code">Kind of problem.</param>
/// <param name="Path">Where: <c>name</c>, <c>conditions</c>, <c>conditions[2]</c>, <c>actions[0].lines[1]</c>.</param>
/// <param name="Message">A human-readable English sentence.</param>
public sealed record RuleProblem(RuleProblemSeverity Severity, RuleProblemCode Code, string Path, string Message);

/// <summary>What the validator may check references against. Null sets skip the check.</summary>
/// <param name="CategoryIds">Categories that exist (and are not deleted).</param>
/// <param name="AccountIds">Accounts that exist.</param>
public sealed record RuleValidationContext(IReadOnlySet<Guid>? CategoryIds = null, IReadOnlySet<Guid>? AccountIds = null);

/// <summary>Regular-expression settings for rule conditions.</summary>
public static class RuleRegex
{
    /// <summary>Longest accepted pattern.</summary>
    public const int MaxPatternLength = 500;

    /// <summary>Per-match timeout; a match that takes longer counts as "no match" and is reported in the trace.</summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Options: case-insensitive, culture-invariant.</summary>
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Compiles a pattern with a match timeout; returns null and an English error when it is not valid.</summary>
    public static Regex? TryCreate(string? pattern, TimeSpan matchTimeout, out string? error)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            error = "the pattern is empty";
            return null;
        }

        if (pattern.Length > MaxPatternLength)
        {
            error = $"the pattern is longer than {MaxPatternLength} characters";
            return null;
        }

        try
        {
            error = null;
            return new Regex(pattern, Options, matchTimeout);
        }
        catch (ArgumentException ex)
        {
            error = ex is RegexParseException parse
                ? $"{DescribeParseError(parse.Error)} at position {parse.Offset.ToString(CultureInfo.InvariantCulture)}"
                : ex.Message;
            return null;
        }
    }

    private static string DescribeParseError(RegexParseError error)
    {
        var words = new System.Text.StringBuilder();
        foreach (var c in error.ToString())
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(c));
        }

        return words.ToString();
    }
}

/// <summary>
/// Checks a rule and reports human-readable problems (F-TXN-4). Errors make the
/// <see cref="RuleEngine"/> skip the rule; warnings are for the rule editor.
/// </summary>
public static class RuleValidator
{
    /// <summary>Validates a rule. The result is empty when there is nothing to report.</summary>
    public static IReadOnlyList<RuleProblem> Validate(RuleDefinition rule, RuleValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var problems = new List<RuleProblem>();

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            problems.Add(Error(RuleProblemCode.NameMissing, "name", "Give the rule a name."));
        }

        var conditions = rule.Conditions.Conditions;
        if (conditions.Count == 0)
        {
            problems.Add(Error(RuleProblemCode.NoConditions, "conditions", "Add at least one condition; a rule without conditions would match every transaction."));
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            ValidateCondition(conditions[i], $"conditions[{i}]", $"Condition {i + 1}", context, problems);
        }

        var actions = rule.Actions.Actions;
        if (actions.Count == 0)
        {
            problems.Add(Error(RuleProblemCode.NoActions, "actions", "Add at least one action; this rule would not change anything."));
        }

        for (var i = 0; i < actions.Count; i++)
        {
            ValidateAction(actions[i], $"actions[{i}]", $"Action {i + 1}", context, problems);
        }

        CheckConflicts(actions, problems);
        return problems;
    }

    /// <summary>True when <see cref="Validate"/> reports no errors (warnings are allowed).</summary>
    public static bool IsValid(RuleDefinition rule, RuleValidationContext? context = null) =>
        Validate(rule, context).All(p => p.Severity != RuleProblemSeverity.Error);

    private static void ValidateCondition(RuleCondition condition, string path, string label, RuleValidationContext? context, List<RuleProblem> problems)
    {
        switch (condition)
        {
            case PayeeCondition payee:
                ValidateText(payee.Operator, payee.Value, path, label, "payee", problems);
                break;
            case MemoCondition memo:
                ValidateText(memo.Operator, memo.Value, path, label, "memo", problems);
                break;
            case AmountCondition amount:
                ValidateAmount(amount, path, label, problems);
                break;
            case DirectionCondition:
                break;
            case AccountCondition account:
                if (account.AccountIds is null || account.AccountIds.Count == 0)
                {
                    problems.Add(Error(RuleProblemCode.EmptySet, path, $"{label}: choose at least one account."));
                    break;
                }

                foreach (var id in account.AccountIds)
                {
                    CheckAccount(id, path, label, context, problems);
                }

                break;
            case SourceCondition source:
                if (source.Sources is null || source.Sources.Count == 0)
                {
                    problems.Add(Error(RuleProblemCode.EmptySet, path, $"{label}: choose at least one source."));
                }

                break;
            case DateRangeCondition range:
                if (range.From is null && range.To is null)
                {
                    problems.Add(Error(RuleProblemCode.OpenDateRange, path, $"{label}: set a start date, an end date, or both."));
                }
                else if (range.From > range.To)
                {
                    problems.Add(Error(RuleProblemCode.ReversedDateRange, path, $"{label}: the end date {range.To:yyyy-MM-dd} is before the start date {range.From:yyyy-MM-dd}."));
                }

                break;
            case TagCondition tag:
                if (string.IsNullOrWhiteSpace(tag.Tag))
                {
                    problems.Add(Error(RuleProblemCode.EmptyTag, path, $"{label}: enter a tag name."));
                }

                break;
        }
    }

    private static void ValidateText(TextOperator op, string? value, string path, string label, string field, List<RuleProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(Error(RuleProblemCode.EmptyText, path, $"{label}: enter the {field} text to compare with."));
            return;
        }

        if (op != TextOperator.Regex)
        {
            return;
        }

        if (value.Length > RuleRegex.MaxPatternLength)
        {
            problems.Add(Error(RuleProblemCode.RegexTooLong, path, $"{label}: the regular expression is longer than {RuleRegex.MaxPatternLength} characters."));
            return;
        }

        if (RuleRegex.TryCreate(value, RuleRegex.DefaultMatchTimeout, out var error) is null)
        {
            problems.Add(Error(RuleProblemCode.InvalidRegex, path, $"{label}: the regular expression is not valid ({error})."));
        }
    }

    private static void ValidateAmount(AmountCondition amount, string path, string label, List<RuleProblem> problems)
    {
        if (!amount.Signed && (amount.Amount < 0 || amount.AmountMax < 0))
        {
            problems.Add(Error(RuleProblemCode.NegativeAmount, path, $"{label}: amounts compare the size of the transaction, so enter a positive number (use a direction condition for inflow or outflow)."));
        }

        if (amount.Operator == AmountOperator.Between)
        {
            if (amount.AmountMax is not { } max)
            {
                problems.Add(Error(RuleProblemCode.MissingUpperBound, path, $"{label}: enter the upper amount of the range."));
            }
            else if (amount.Amount > max)
            {
                problems.Add(Error(RuleProblemCode.ReversedBounds, path, $"{label}: the lower amount is greater than the upper amount."));
            }
        }
        else if (amount.AmountMax is not null)
        {
            problems.Add(Warning(RuleProblemCode.UnusedUpperBound, path, $"{label}: the upper amount is ignored unless the comparison is \"between\"."));
        }
    }

    private static void ValidateAction(RuleAction action, string path, string label, RuleValidationContext? context, List<RuleProblem> problems)
    {
        switch (action)
        {
            case SetPayeeAction setPayee:
                if (string.IsNullOrWhiteSpace(setPayee.Payee))
                {
                    problems.Add(Error(RuleProblemCode.EmptyPayee, path, $"{label}: enter the new payee name."));
                }

                break;
            case SetCategoryAction setCategory:
                CheckCategory(setCategory.CategoryId, path, label, context, problems);
                break;
            case SetMemoAction setMemo:
                if (setMemo.Memo is null)
                {
                    problems.Add(Error(RuleProblemCode.EmptyText, path, $"{label}: enter the memo (leave it empty to clear the memo)."));
                }

                break;
            case AppendMemoAction append:
                if (string.IsNullOrWhiteSpace(append.Text))
                {
                    problems.Add(Error(RuleProblemCode.EmptyAppendText, path, $"{label}: enter the text to append to the memo."));
                }

                break;
            case AddTagAction addTag:
                if (string.IsNullOrWhiteSpace(addTag.Tag))
                {
                    problems.Add(Error(RuleProblemCode.EmptyTag, path, $"{label}: enter a tag name."));
                }

                break;
            case SplitByAmountsAction byAmounts:
                ValidateAmountSplit(byAmounts, path, label, context, problems);
                break;
            case SplitByPercentagesAction byPercent:
                ValidatePercentSplit(byPercent, path, label, context, problems);
                break;
            case SetTransferAccountAction transfer:
                CheckAccount(transfer.AccountId, path, label, context, problems);
                break;
        }
    }

    private static void ValidateAmountSplit(SplitByAmountsAction split, string path, string label, RuleValidationContext? context, List<RuleProblem> problems)
    {
        var lines = split.Lines ?? [];
        if (lines.Count < 2)
        {
            problems.Add(Error(RuleProblemCode.TooFewSplitLines, path, $"{label}: a split needs at least two lines."));
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var linePath = $"{path}.lines[{i}]";
            var lineLabel = $"{label}, line {i + 1}";
            var isLast = i == lines.Count - 1;
            if (isLast && lines[i].Amount is not null)
            {
                problems.Add(Error(RuleProblemCode.LastSplitLineHasAmount, linePath, $"{lineLabel}: the last line always receives the rest of the amount; leave its amount empty."));
            }
            else if (!isLast && lines[i].Amount is not > 0)
            {
                problems.Add(Error(RuleProblemCode.InvalidSplitAmount, linePath, $"{lineLabel}: enter an amount greater than zero."));
            }

            if (lines[i].CategoryId is { } category)
            {
                CheckCategory(category, linePath, lineLabel, context, problems);
            }
        }
    }

    private static void ValidatePercentSplit(SplitByPercentagesAction split, string path, string label, RuleValidationContext? context, List<RuleProblem> problems)
    {
        var lines = split.Lines ?? [];
        if (lines.Count < 2)
        {
            problems.Add(Error(RuleProblemCode.TooFewSplitLines, path, $"{label}: a split needs at least two lines."));
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var linePath = $"{path}.lines[{i}]";
            var lineLabel = $"{label}, line {i + 1}";
            if (lines[i].Percent <= 0)
            {
                problems.Add(Error(RuleProblemCode.InvalidSplitPercent, linePath, $"{lineLabel}: enter a percentage greater than zero."));
            }

            if (lines[i].CategoryId is { } category)
            {
                CheckCategory(category, linePath, lineLabel, context, problems);
            }
        }

        var total = lines.Sum(l => l.Percent);
        if (total != 100m)
        {
            problems.Add(Error(RuleProblemCode.PercentagesNotHundred, path, $"{label}: the percentages add up to {total.ToString("0.##", CultureInfo.InvariantCulture)}%; they must add up to 100%."));
        }
    }

    private static void CheckConflicts(IReadOnlyList<RuleAction> actions, List<RuleProblem> problems)
    {
        // Actions that each replace one field: a later one overrides an earlier one.
        static string? Slot(RuleAction action) => action switch
        {
            SetPayeeAction => "payee",
            SetCategoryAction or SplitByAmountsAction or SplitByPercentagesAction => "category",
            SetMemoAction => "memo",
            SetTransferAccountAction => "transfer",
            _ => null,
        };

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < actions.Count; i++)
        {
            if (Slot(actions[i]) is not { } slot)
            {
                continue;
            }

            if (seen.TryGetValue(slot, out var earlier))
            {
                var what = slot == "category" ? "the category or split" : "the " + slot;
                problems.Add(Warning(
                    RuleProblemCode.ConflictingActions,
                    $"actions[{i}]",
                    $"Action {i + 1} sets {what} again; it replaces what action {earlier + 1} did."));
            }

            seen[slot] = i;
        }
    }

    private static void CheckCategory(Guid id, string path, string label, RuleValidationContext? context, List<RuleProblem> problems)
    {
        if (id == Guid.Empty)
        {
            problems.Add(Error(RuleProblemCode.EmptyId, path, $"{label}: choose a category."));
        }
        else if (context?.CategoryIds is { } known && !known.Contains(id))
        {
            problems.Add(Error(RuleProblemCode.UnknownCategory, path, $"{label}: the category no longer exists; choose another one."));
        }
    }

    private static void CheckAccount(Guid id, string path, string label, RuleValidationContext? context, List<RuleProblem> problems)
    {
        if (id == Guid.Empty)
        {
            problems.Add(Error(RuleProblemCode.EmptyId, path, $"{label}: choose an account."));
        }
        else if (context?.AccountIds is { } known && !known.Contains(id))
        {
            problems.Add(Error(RuleProblemCode.UnknownAccount, path, $"{label}: the account no longer exists; choose another one."));
        }
    }

    private static RuleProblem Error(RuleProblemCode code, string path, string message) =>
        new(RuleProblemSeverity.Error, code, path, message);

    private static RuleProblem Warning(RuleProblemCode code, string path, string message) =>
        new(RuleProblemSeverity.Warning, code, path, message);
}
