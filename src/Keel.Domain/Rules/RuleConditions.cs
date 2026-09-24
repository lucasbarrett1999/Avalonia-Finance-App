using System.Text.Json.Serialization;

namespace Keel.Domain.Rules;

/// <summary>How the conditions of a rule combine.</summary>
public enum RuleMatchMode
{
    /// <summary>Every condition must match.</summary>
    All,

    /// <summary>At least one condition must match.</summary>
    Any,
}

/// <summary>Text comparison used by payee and memo conditions. Comparisons ignore case.</summary>
public enum TextOperator
{
    /// <summary>The text contains the value.</summary>
    Contains,

    /// <summary>The text equals the value.</summary>
    [JsonStringEnumMemberName("equals")]
    EqualTo,

    /// <summary>The text starts with the value.</summary>
    StartsWith,

    /// <summary>The value is a .NET regular expression that matches somewhere in the text.</summary>
    Regex,
}

/// <summary>Amount comparison used by <see cref="AmountCondition"/>.</summary>
public enum AmountOperator
{
    /// <summary>Amount equals <see cref="AmountCondition.Amount"/>.</summary>
    [JsonStringEnumMemberName("equals")]
    EqualTo,

    /// <summary>Amount is between <see cref="AmountCondition.Amount"/> and <see cref="AmountCondition.AmountMax"/>, inclusive.</summary>
    Between,

    /// <summary>Amount is strictly greater than <see cref="AmountCondition.Amount"/>.</summary>
    GreaterThan,

    /// <summary>Amount is strictly less than <see cref="AmountCondition.Amount"/>.</summary>
    LessThan,
}

/// <summary>Money direction (PRD 6.1 sign convention).</summary>
public enum TransactionDirection
{
    /// <summary>Positive amount.</summary>
    Inflow,

    /// <summary>Negative amount.</summary>
    Outflow,
}

/// <summary>
/// The condition half of a rule (<c>Rule.ConditionsJson</c>): conditions combined with
/// all/any. Serialized as versioned JSON by <see cref="RuleJson"/>.
/// </summary>
public sealed record RuleConditionSet
{
    /// <summary>Format version of the JSON document.</summary>
    public int Version { get; init; } = RuleJson.CurrentVersion;

    /// <summary>How the conditions combine.</summary>
    public RuleMatchMode Match { get; init; } = RuleMatchMode.All;

    /// <summary>The conditions, in display order.</summary>
    public IReadOnlyList<RuleCondition> Conditions { get; init; } = [];
}

/// <summary>
/// One rule condition. The JSON <c>type</c> discriminator names the concrete kind; new kinds
/// may be added in later format versions, never renamed.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PayeeCondition), "payee")]
[JsonDerivedType(typeof(MemoCondition), "memo")]
[JsonDerivedType(typeof(AmountCondition), "amount")]
[JsonDerivedType(typeof(DirectionCondition), "direction")]
[JsonDerivedType(typeof(AccountCondition), "account")]
[JsonDerivedType(typeof(SourceCondition), "source")]
[JsonDerivedType(typeof(DateRangeCondition), "dateRange")]
[JsonDerivedType(typeof(TagCondition), "tag")]
public abstract record RuleCondition
{
    /// <summary>A short English description such as <c>payee contains "trader"</c>.</summary>
    public abstract string Describe(RuleNames? names = null);
}

/// <summary>
/// Payee text condition. It matches when either the current payee name or the raw descriptor
/// satisfies it. With <see cref="Normalized"/>, the payees go through
/// <see cref="Import.PayeeNormalizer"/> first, and so does <see cref="Value"/> unless the operator
/// is <see cref="TextOperator.Regex"/> (a pattern is used as written against the normalized payee).
/// </summary>
/// <param name="Operator">Comparison.</param>
/// <param name="Value">Text or pattern.</param>
/// <param name="Normalized">Compare normalized payees (upper-case, noise removed).</param>
public sealed record PayeeCondition(TextOperator Operator, string Value, bool Normalized = false) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        (Normalized ? "normalized payee " : "payee ") + RuleNames.DescribeText(Operator, Value);
}

/// <summary>Memo text condition (a missing memo is empty text).</summary>
/// <param name="Operator">Comparison.</param>
/// <param name="Value">Text or pattern.</param>
public sealed record MemoCondition(TextOperator Operator, string Value) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => "memo " + RuleNames.DescribeText(Operator, Value);
}

/// <summary>
/// Amount condition in minor units. By default it compares the magnitude (a $12.50 purchase and
/// a $12.50 refund both have amount 1250); <see cref="Signed"/> compares the signed amount.
/// </summary>
/// <param name="Operator">Comparison.</param>
/// <param name="Amount">Value, or the lower bound for <see cref="AmountOperator.Between"/>.</param>
/// <param name="AmountMax">Upper bound for <see cref="AmountOperator.Between"/>.</param>
/// <param name="Signed">Compare the signed amount instead of the magnitude.</param>
public sealed record AmountCondition(AmountOperator Operator, long Amount, long? AmountMax = null, bool Signed = false) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null)
    {
        var subject = Signed ? "signed amount" : "amount";
        var n = names ?? RuleNames.Empty;
        return Operator switch
        {
            AmountOperator.EqualTo => $"{subject} is {n.FormatAmount(Amount)}",
            AmountOperator.Between => $"{subject} is between {n.FormatAmount(Amount)} and {n.FormatAmount(AmountMax ?? Amount)}",
            AmountOperator.GreaterThan => $"{subject} is more than {n.FormatAmount(Amount)}",
            AmountOperator.LessThan => $"{subject} is less than {n.FormatAmount(Amount)}",
            _ => subject,
        };
    }
}

/// <summary>Inflow (amount &gt; 0) or outflow (amount &lt; 0). A zero amount matches neither.</summary>
/// <param name="Direction">Direction.</param>
public sealed record DirectionCondition(TransactionDirection Direction) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        Direction == TransactionDirection.Inflow ? "is an inflow" : "is an outflow";
}

/// <summary>The transaction's account is one of <see cref="AccountIds"/>.</summary>
/// <param name="AccountIds">Accounts.</param>
public sealed record AccountCondition(IReadOnlyList<Guid> AccountIds) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        "account is " + RuleNames.JoinOr(AccountIds.Select(id => (names ?? RuleNames.Empty).Account(id)));
}

/// <summary>The transaction's source is one of <see cref="Sources"/>.</summary>
/// <param name="Sources">Sources.</param>
public sealed record SourceCondition(IReadOnlyList<TransactionSource> Sources) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) =>
        "source is " + RuleNames.JoinOr(Sources.Select(s => s switch
        {
            TransactionSource.Manual => "manual entry",
            TransactionSource.File => "file import",
            TransactionSource.Provider => "bank sync",
            TransactionSource.System => "system",
            TransactionSource.Scheduled => "scheduled",
            _ => s.ToString(),
        }));
}

/// <summary>The date is within [<see cref="From"/>, <see cref="To"/>]; a missing bound is open.</summary>
/// <param name="From">First date, inclusive.</param>
/// <param name="To">Last date, inclusive.</param>
public sealed record DateRangeCondition(DateOnly? From = null, DateOnly? To = null) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => (From, To) switch
    {
        ({ } f, { } t) => $"date is between {f:yyyy-MM-dd} and {t:yyyy-MM-dd}",
        ({ } f, null) => $"date is on or after {f:yyyy-MM-dd}",
        (null, { } t) => $"date is on or before {t:yyyy-MM-dd}",
        _ => "any date",
    };
}

/// <summary>The transaction carries the tag (case-insensitive).</summary>
/// <param name="Tag">Tag name.</param>
public sealed record TagCondition(string Tag) : RuleCondition
{
    /// <inheritdoc />
    public override string Describe(RuleNames? names = null) => $"has tag \"{Tag}\"";
}
