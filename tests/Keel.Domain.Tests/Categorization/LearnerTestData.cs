using Keel.Domain.Categorization;
using Keel.Domain.Rules;

namespace Keel.Domain.Tests.Categorization;

internal static class LearnerTestData
{
    public static readonly Guid Checking = new("00000000-0000-7000-8000-00000000b001");
    public static readonly Guid Visa = new("00000000-0000-7000-8000-00000000b002");
    public static readonly Guid Groceries = new("00000000-0000-7000-8000-00000000d001");
    public static readonly Guid Household = new("00000000-0000-7000-8000-00000000d002");
    public static readonly Guid Dining = new("00000000-0000-7000-8000-00000000d003");
    public static readonly Guid Hidden = new("00000000-0000-7000-8000-00000000d004");
    public static readonly Guid CardPayment = new("00000000-0000-7000-8000-00000000d005");
    public static readonly Guid Electronics = new("00000000-0000-7000-8000-00000000d006");

    public static IReadOnlyList<LearnerCategory> Catalog { get; } =
    [
        new(Groceries, "Groceries", false),
        new(Household, "Household", false),
        new(Dining, "Dining Out", false),
        new(Hidden, "Old Stuff", true),
        new(CardPayment, "Visa Payment", true),
        new(Electronics, "Electronics", false),
    ];

    private static int _sequence;

    public static TransactionSnapshot Txn(string payee, long amount = -4200, Guid? account = null, DateOnly? date = null) => new()
    {
        Id = new Guid(Interlocked.Increment(ref _sequence), 0, 0, new byte[8]),
        AccountId = account ?? Visa,
        Date = date ?? new DateOnly(2026, 9, 12),
        Amount = amount,
        PayeeRaw = payee,
        Payee = payee,
        Source = TransactionSource.File,
    };

    public static LabeledExample Ex(string payee, Guid category, long amount = -4200, Guid? account = null, DateOnly? date = null) =>
        new(Txn(payee, amount, account, date), category);

    public static IEnumerable<LabeledExample> Repeat(int count, string payee, Guid category, long amount = -4200, Guid? account = null) =>
        Enumerable.Range(0, count).Select(i => Ex(payee, category, amount + (i % 5 * 37), account, new DateOnly(2026, 1, 1).AddDays(i * 3)));
}
