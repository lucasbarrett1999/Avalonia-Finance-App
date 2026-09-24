using System.Globalization;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Budgeting;

/// <summary>
/// Produces <see cref="BudgetCalculator"/> input from the database with SQL <c>GROUP BY</c>
/// (PRD 6.4.9): no transaction row is loaded into memory. A split parent contributes nothing
/// itself; each split counts with its own category. Soft-deleted transactions and their splits
/// are excluded.
/// </summary>
/// <remarks>
/// The ledger queries are raw SQL because the plan matters at 100k rows: EF's translation groups
/// through <c>IX_Transactions_CategoryId_Date</c> (a random table lookup per row) and computes the
/// month with two <c>strftime</c> calls, which measured 3.5× slower than a plain table scan
/// (<c>NOT INDEXED</c>) grouped by <c>substr(Date, 1, 7)</c>. Raw SQL bypasses the global query
/// filters, so every query states <c>IsDeleted = 0</c> itself; the tests in
/// <c>BudgetAggregationQueryTests</c> pin the soft-delete, split and transfer rules. Table or
/// column renames in a migration must update these statements.
/// </remarks>
public static class BudgetAggregationQuery
{
    /// <summary>Loads accounts, groups, categories, aggregated activity, assignments and card transfers.</summary>
    public static async Task<BudgetInput> LoadInputAsync(KeelDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var accounts = (await db.Accounts.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(BudgetAccount.From).ToList();
        var accountsById = accounts.ToDictionary(a => a.Id);
        var groups = (await db.CategoryGroups.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).Select(BudgetGroup.From).ToList();
        var categories = (await db.Categories.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .Select(c => BudgetCategory.From(c, accountsById)).ToList();

        var activity = await ActivityAsync(db, ct).ConfigureAwait(false);
        var assignments = await db.BudgetAssignments.AsNoTracking()
            .Select(a => new AssignmentTotal(a.CategoryId, a.Month, a.Assigned))
            .ToListAsync(ct).ConfigureAwait(false);
        var transfers = await CardTransfersAsync(db, ct).ConfigureAwait(false);

        return new BudgetInput(accounts, groups, categories, activity, assignments, transfers);
    }

    /// <summary>
    /// Σ Amount per (category, month, account) over on-budget accounts: categorized rows, and
    /// uncategorized rows that are not transfers (with a null category). Transfers without a
    /// category (between on-budget accounts, or the tracking side) are not activity. A key can
    /// appear twice (once from parents, once from splits); the calculator sums duplicates.
    /// </summary>
    public static async Task<IReadOnlyList<ActivityTotal>> ActivityAsync(KeelDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var parents = await db.Database.SqlQueryRaw<ActivityRow>(ParentActivitySql).ToListAsync(ct).ConfigureAwait(false);
        var splits = await db.Database.SqlQueryRaw<ActivityRow>(SplitActivitySql).ToListAsync(ct).ConfigureAwait(false);
        return [.. parents.Concat(splits).Select(r => new ActivityTotal(r.CategoryId, r.Month, r.AccountId, r.Amount))];
    }

    /// <summary>
    /// Σ Amount of uncategorized transfer rows INTO on-budget credit accounts per (card, source
    /// account, month): the card side (positive) of payments. The calculator keeps those whose
    /// source is an on-budget cash account.
    /// </summary>
    public static async Task<IReadOnlyList<CardTransferTotal>> CardTransfersAsync(KeelDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var parents = await db.Database.SqlQueryRaw<CardTransferRow>(ParentCardTransferSql).ToListAsync(ct).ConfigureAwait(false);
        var splits = await db.Database.SqlQueryRaw<CardTransferRow>(SplitCardTransferSql).ToListAsync(ct).ConfigureAwait(false);
        return [.. parents.Concat(splits).Select(r => new CardTransferTotal(r.CardAccountId, r.FromAccountId, r.Month, r.Amount))];
    }

    /// <summary>
    /// Ledger balance of every on-budget credit account at the end of each month from
    /// <paramref name="from"/> to <paramref name="to"/>, from monthly net amounts summed in SQL.
    /// </summary>
    public static async Task<IReadOnlyDictionary<DateOnly, Dictionary<Guid, long>>> CardBalancesAsync(
        KeelDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var end = BudgetMonth.NextOf(to);
        var rows = await db.Database.SqlQueryRaw<AccountMonthRow>(CardMonthlyNetSql, end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<DateOnly, Dictionary<Guid, long>>();
        for (var month = BudgetMonth.Of(from); month <= to; month = month.AddMonths(1))
        {
            var monthEnd = month.AddMonths(1);
            result[month] = rows
                .Where(r => r.Month < monthEnd)
                .GroupBy(r => r.AccountId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        }

        return result;
    }

    private const string CreditTypes = "('" + nameof(AccountType.CreditCard) + "', '" + nameof(AccountType.LineOfCredit) + "')";

    /// <summary>Parents without splits; the month is the ISO text prefix of the date.</summary>
    internal const string ParentActivitySql = """
        SELECT t."CategoryId" AS "CategoryId", substr(t."Date", 1, 7) || '-01' AS "Month", t."AccountId" AS "AccountId", SUM(t."Amount") AS "Amount"
        FROM "Transactions" AS t NOT INDEXED
        INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
        WHERE t."IsDeleted" = 0 AND a."IsOnBudget" = 1
          AND (t."CategoryId" IS NOT NULL OR t."TransferAccountId" IS NULL)
          AND NOT EXISTS (SELECT 1 FROM "TransactionSplits" AS s WHERE s."TransactionId" = t."Id")
        GROUP BY t."CategoryId", substr(t."Date", 1, 7), t."AccountId"
        """;

    /// <summary>Splits of non-deleted parents, each with its own category.</summary>
    internal const string SplitActivitySql = """
        SELECT s."CategoryId" AS "CategoryId", substr(t."Date", 1, 7) || '-01' AS "Month", t."AccountId" AS "AccountId", SUM(s."Amount") AS "Amount"
        FROM "TransactionSplits" AS s
        INNER JOIN "Transactions" AS t ON t."Id" = s."TransactionId"
        INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
        WHERE t."IsDeleted" = 0 AND a."IsOnBudget" = 1
          AND (s."CategoryId" IS NOT NULL OR s."TransferAccountId" IS NULL)
        GROUP BY s."CategoryId", substr(t."Date", 1, 7), t."AccountId"
        """;

    /// <summary>Card side of uncategorized transfers into a credit account (parents without splits).</summary>
    internal const string ParentCardTransferSql = """
        SELECT t."AccountId" AS "CardAccountId", t."TransferAccountId" AS "FromAccountId", substr(t."Date", 1, 7) || '-01' AS "Month", SUM(t."Amount") AS "Amount"
        FROM "Transactions" AS t
        INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
        WHERE t."IsDeleted" = 0 AND a."IsOnBudget" = 1 AND a."Type" IN
        """ + CreditTypes + """

          AND t."CategoryId" IS NULL AND t."TransferAccountId" IS NOT NULL AND t."Amount" > 0
          AND NOT EXISTS (SELECT 1 FROM "TransactionSplits" AS s WHERE s."TransactionId" = t."Id")
        GROUP BY t."AccountId", t."TransferAccountId", substr(t."Date", 1, 7)
        """;

    /// <summary>Card side of uncategorized transfer splits into a credit account.</summary>
    internal const string SplitCardTransferSql = """
        SELECT t."AccountId" AS "CardAccountId", s."TransferAccountId" AS "FromAccountId", substr(t."Date", 1, 7) || '-01' AS "Month", SUM(s."Amount") AS "Amount"
        FROM "TransactionSplits" AS s
        INNER JOIN "Transactions" AS t ON t."Id" = s."TransactionId"
        INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
        WHERE t."IsDeleted" = 0 AND a."IsOnBudget" = 1 AND a."Type" IN
        """ + CreditTypes + """

          AND s."CategoryId" IS NULL AND s."TransferAccountId" IS NOT NULL AND s."Amount" > 0
        GROUP BY t."AccountId", s."TransferAccountId", substr(t."Date", 1, 7)
        """;

    /// <summary>Net amount per credit account and month before an exclusive end date ({0}).</summary>
    internal const string CardMonthlyNetSql = """
        SELECT t."AccountId" AS "AccountId", substr(t."Date", 1, 7) || '-01' AS "Month", SUM(t."Amount") AS "Amount"
        FROM "Transactions" AS t
        INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
        WHERE t."IsDeleted" = 0 AND a."IsOnBudget" = 1 AND a."Type" IN
        """ + CreditTypes + """

          AND t."Date" < {0}
        GROUP BY t."AccountId", substr(t."Date", 1, 7)
        """;

    internal sealed record ActivityRow(Guid? CategoryId, DateOnly Month, Guid AccountId, long Amount);

    internal sealed record CardTransferRow(Guid CardAccountId, Guid FromAccountId, DateOnly Month, long Amount);

    internal sealed record AccountMonthRow(Guid AccountId, DateOnly Month, long Amount);
}
