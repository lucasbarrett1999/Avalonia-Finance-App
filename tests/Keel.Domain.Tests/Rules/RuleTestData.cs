using Keel.Domain.Rules;

namespace Keel.Domain.Tests.Rules;

/// <summary>Fixed ids and builders shared by the rule tests.</summary>
internal static class RuleTestData
{
    public static readonly Guid Checking = new("00000000-0000-7000-8000-00000000a001");
    public static readonly Guid Visa = new("00000000-0000-7000-8000-00000000a002");
    public static readonly Guid Savings = new("00000000-0000-7000-8000-00000000a003");
    public static readonly Guid Groceries = new("00000000-0000-7000-8000-00000000c001");
    public static readonly Guid Dining = new("00000000-0000-7000-8000-00000000c002");
    public static readonly Guid Household = new("00000000-0000-7000-8000-00000000c003");
    public static readonly Guid Gifts = new("00000000-0000-7000-8000-00000000c004");

    public static RuleNames Names { get; } = new(
        new Dictionary<Guid, string> { [Groceries] = "Groceries", [Dining] = "Dining Out", [Household] = "Household", [Gifts] = "Gifts" },
        new Dictionary<Guid, string> { [Checking] = "Checking", [Visa] = "Visa", [Savings] = "Savings" });

    public static TransactionSnapshot Txn(
        string payee = "TRADER JOE'S #552 BROOKLYN NY",
        long amount = -5423,
        Guid? account = null,
        DateOnly? date = null,
        string? memo = null,
        TransactionSource source = TransactionSource.File,
        IReadOnlyList<string>? tags = null) => new()
        {
            Id = new Guid("00000000-0000-7000-8000-0000000f0001"),
            AccountId = account ?? Checking,
            Date = date ?? new DateOnly(2026, 9, 14),
            Amount = amount,
            PayeeRaw = payee,
            Payee = payee,
            Memo = memo,
            Source = source,
            Tags = tags ?? [],
        };

    public static RuleDefinition Rule(
        string name,
        IReadOnlyList<RuleCondition> conditions,
        IReadOnlyList<RuleAction> actions,
        int sortOrder = 0,
        bool continueAfterMatch = false,
        bool enabled = true,
        RuleMatchMode match = RuleMatchMode.All) => new()
        {
            Id = IdFor(name),
            Name = name,
            SortOrder = sortOrder,
            IsEnabled = enabled,
            ContinueAfterMatch = continueAfterMatch,
            Conditions = new RuleConditionSet { Match = match, Conditions = conditions },
            Actions = new RuleActionSet { Actions = actions },
        };

    public static Guid IdFor(string name)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static bool Matches(RuleCondition condition, TransactionSnapshot snapshot) =>
        RuleEngine.Apply(snapshot, [Rule("probe", [condition], [new FlagAction()])])
            .Trace.Evaluations.Single().Outcome == RuleOutcome.Matched;

    public static TransactionSnapshot ApplyAction(RuleAction action, TransactionSnapshot snapshot) =>
        RuleEngine.Apply(snapshot, [Rule("probe", [new AmountCondition(AmountOperator.LessThan, long.MaxValue)], [action])]).Result;
}
