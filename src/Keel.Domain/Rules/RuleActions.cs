using System.Text.Json.Serialization;

namespace Keel.Domain.Rules;

/// <summary>
/// The action half of a rule (<c>Rule.ActionsJson</c>): actions applied in order when the rule
/// matches. Serialized as versioned JSON by <see cref="RuleJson"/>.
/// </summary>
public sealed record RuleActionSet
{
    /// <summary>Format version of the JSON document.</summary>
    public int Version { get; init; } = RuleJson.CurrentVersion;

    /// <summary>The actions, applied in this order.</summary>
    public IReadOnlyList<RuleAction> Actions { get; init; } = [];
}

/// <summary>
/// One rule action. The JSON <c>type</c> discriminator names the concrete kind; new kinds may be
/// added in later format versions, never renamed.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetPayeeAction), "setPayee")]
[JsonDerivedType(typeof(SetCategoryAction), "setCategory")]
[JsonDerivedType(typeof(SetMemoAction), "setMemo")]
[JsonDerivedType(typeof(AppendMemoAction), "appendMemo")]
[JsonDerivedType(typeof(AddTagAction), "addTag")]
[JsonDerivedType(typeof(MarkApprovedAction), "markApproved")]
[JsonDerivedType(typeof(FlagAction), "flag")]
[JsonDerivedType(typeof(SplitByAmountsAction), "splitByAmounts")]
[JsonDerivedType(typeof(SplitByPercentagesAction), "splitByPercentages")]
[JsonDerivedType(typeof(SetTransferAccountAction), "setTransferAccount")]
public abstract record RuleAction
{
    /// <summary>A short English description such as <c>set category to Groceries</c>.</summary>
    public abstract string Describe(RuleNames? names = null);
}

/// <summary>Renames the payee (the raw descriptor is kept).</summary>
/// <param name="Payee">New payee name.</param>
public sealed record SetPayeeAction(string Payee) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => $"set payee to \"{Payee}\"";
}

/// <summary>Sets the category. On a split transaction this replaces the splits.</summary>
/// <param name="CategoryId">Category.</param>
public sealed record SetCategoryAction(Guid CategoryId) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        "set category to " + (names ?? RuleNames.Empty).Category(CategoryId);
}

/// <summary>Replaces the memo (an empty memo clears it).</summary>
/// <param name="Memo">New memo.</param>
public sealed record SetMemoAction(string Memo) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        Memo.Length == 0 ? "clear memo" : $"set memo to \"{Memo}\"";
}

/// <summary>Appends text to the memo, separated by a space when the memo is not empty. Skipped when the memo already ends with the text.</summary>
/// <param name="Text">Text to append.</param>
public sealed record AppendMemoAction(string Text) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => $"append \"{Text}\" to memo";
}

/// <summary>Adds a tag (no-op when the tag is already present, ignoring case).</summary>
/// <param name="Tag">Tag name.</param>
public sealed record AddTagAction(string Tag) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => $"add tag \"{Tag}\"";
}

/// <summary>Marks the transaction approved, so it skips the review queue.</summary>
public sealed record MarkApprovedAction : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => "mark approved";
}

/// <summary>Flags the transaction for attention.</summary>
public sealed record FlagAction : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => "flag";
}

/// <summary>
/// Splits the transaction by fixed amounts. Every line but the last has a positive
/// <see cref="AmountSplitLine.Amount"/> (a magnitude that takes the transaction's sign); the last
/// line has none and receives the remainder, so the splits sum exactly to the parent. When the
/// fixed amounts leave nothing (or less than nothing) for the last line, the action is skipped.
/// </summary>
/// <param name="Lines">At least two lines.</param>
public sealed record SplitByAmountsAction(IReadOnlyList<AmountSplitLine> Lines) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null)
    {
        var n = names ?? RuleNames.Empty;
        return "split into " + string.Join(", ", Lines.Select(line =>
            (line.Amount is { } amount ? n.FormatAmount(amount) : "the rest") + " to " + n.Category(line.CategoryId)));
    }
}

/// <summary>One line of <see cref="SplitByAmountsAction"/>.</summary>
/// <param name="CategoryId">Category of the split (null leaves it uncategorized).</param>
/// <param name="Amount">Positive magnitude in minor units; null on the last line.</param>
/// <param name="Memo">Split memo.</param>
public sealed record AmountSplitLine(Guid? CategoryId = null, long? Amount = null, string? Memo = null);

/// <summary>
/// Splits the transaction by percentages that sum to 100. Each line is rounded with banker's
/// rounding and the rounding remainder goes to the last line (PRD 6.1), so the splits sum exactly
/// to the parent.
/// </summary>
/// <param name="Lines">At least two lines.</param>
public sealed record SplitByPercentagesAction(IReadOnlyList<PercentSplitLine> Lines) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null)
    {
        var n = names ?? RuleNames.Empty;
        return "split into " + string.Join(", ", Lines.Select(line =>
            line.Percent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "% to " + n.Category(line.CategoryId)));
    }
}

/// <summary>One line of <see cref="SplitByPercentagesAction"/>.</summary>
/// <param name="CategoryId">Category of the split (null leaves it uncategorized).</param>
/// <param name="Percent">Share in percent, greater than 0.</param>
/// <param name="Memo">Split memo.</param>
public sealed record PercentSplitLine([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? CategoryId, decimal Percent, string? Memo = null);

/// <summary>Makes the transaction a transfer to or from another account (the pairing itself is the ledger service's job).</summary>
/// <param name="AccountId">The other account.</param>
public sealed record SetTransferAccountAction(Guid AccountId) : RuleAction
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        "make a transfer with " + (names ?? RuleNames.Empty).Account(AccountId);
}
