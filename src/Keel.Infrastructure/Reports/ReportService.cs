using System.Data.Common;
using System.Globalization;
using System.Text;
using Keel.Application.Reports;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Domain.Reports;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Reports;

/// <summary>
/// <see cref="IReportService"/> in raw SQL (PRD D6: hot paths use raw SQL): each report is one or
/// two <c>GROUP BY</c> statements over non-deleted transactions and splits (a split parent counts
/// through its lines only), plus small lookups of accounts, categories and snapshots. Raw SQL
/// bypasses the soft-delete query filter, so every statement states <c>IsDeleted = 0</c>; the
/// rules (system rows, transfers, tracking accounts, splits) are pinned by <c>ReportServiceTests</c>
/// and explained in ADR 0060.
/// </summary>
public sealed class ReportService(IDbContextFactory<KeelDbContext> factory, TimeProvider timeProvider) : IReportService
{
    /// <inheritdoc />
    public Task<SpendingReport> GetSpendingAsync(ReportQuery query, CancellationToken ct)
    {
        Validate(query);
        return RunAsync(
            async (db, connection) =>
            {
                var (previousFrom, previousTo) = ReportPeriod.Previous(query.From, query.To);
                using var command = connection.CreateCommand();
                var rows = RowSql(command, query with { From = previousFrom }, "t.\"Date\"");
                Add(command, "@current", Date(query.From));
                command.CommandText = $"""
                    SELECT x."CategoryId", CASE WHEN x."Date" >= @current THEN 1 ELSE 0 END AS "IsCurrent", SUM(x."Amount")
                    FROM ({rows}) AS x
                    GROUP BY x."CategoryId", CASE WHEN x."Date" >= @current THEN 1 ELSE 0 END
                    """;

                var current = new Dictionary<Guid, long>();
                var previous = new Dictionary<Guid, long>();
                long uncategorized = 0, uncategorizedPrevious = 0;
                using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var isCurrent = reader.GetInt64(1) == 1;
                        var spent = -reader.GetInt64(2); // outflows are negative; spending is shown positive
                        if (reader.IsDBNull(0))
                        {
                            if (isCurrent)
                            {
                                uncategorized += spent;
                            }
                            else
                            {
                                uncategorizedPrevious += spent;
                            }

                            continue;
                        }

                        var id = Guid.Parse(reader.GetString(0));
                        var target = isCurrent ? current : previous;
                        target[id] = target.GetValueOrDefault(id) + spent;
                    }
                }

                var categories = await db.Categories.AsNoTracking()
                    .Select(c => new { c.Id, c.Name, c.SortOrder, c.GroupId, GroupName = c.Group!.Name, GroupSort = c.Group.SortOrder })
                    .ToListAsync(ct).ConfigureAwait(false);
                var groups = categories
                    .Where(c => c.GroupId != SystemIds.InflowGroup)
                    .Select(c => new
                    {
                        c.GroupId,
                        c.GroupName,
                        c.GroupSort,
                        Category = new SpendingCategory(c.Id, c.Name, current.GetValueOrDefault(c.Id), previous.GetValueOrDefault(c.Id)),
                        c.SortOrder,
                    })
                    .Where(c => c.Category.Amount != 0 || c.Category.PreviousAmount != 0)
                    .GroupBy(c => (c.GroupId, c.GroupName, c.GroupSort))
                    .Select(g => new SpendingGroup(
                        g.Key.GroupId,
                        g.Key.GroupName,
                        g.Sum(c => c.Category.Amount),
                        g.Sum(c => c.Category.PreviousAmount),
                        [.. g.OrderByDescending(c => c.Category.Amount).ThenBy(c => c.SortOrder).Select(c => c.Category)]))
                    .ToList();
                if (uncategorized != 0 || uncategorizedPrevious != 0)
                {
                    groups.Add(new SpendingGroup(null, null, uncategorized, uncategorizedPrevious, [new SpendingCategory(null, null, uncategorized, uncategorizedPrevious)]));
                }

                var ordered = groups.OrderByDescending(g => g.Amount).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                return new SpendingReport(
                    await CurrencyAsync(db, ct).ConfigureAwait(false),
                    query.From,
                    query.To,
                    previousFrom,
                    previousTo,
                    ordered.Sum(g => g.Amount),
                    ordered.Sum(g => g.PreviousAmount),
                    ordered);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<IncomeExpenseReport> GetIncomeExpenseAsync(ReportQuery query, CancellationToken ct)
    {
        Validate(query);
        return RunAsync(
            async (db, connection) =>
            {
                using var command = connection.CreateCommand();
                var rows = RowSql(command, query, "t.\"Date\"");
                Add(command, "@inflow", Key(SystemIds.InflowGroup));
                command.CommandText = $"""
                    SELECT substr(x."Date", 1, 7) AS "Month",
                           CASE WHEN c."GroupId" = @inflow OR (x."CategoryId" IS NULL AND x."Amount" > 0) THEN 1 ELSE 0 END AS "IsIncome",
                           SUM(x."Amount")
                    FROM ({rows}) AS x
                    LEFT JOIN "Categories" AS c ON c."Id" = x."CategoryId"
                    GROUP BY 1, 2
                    """;

                var income = new Dictionary<DateOnly, long>();
                var expense = new Dictionary<DateOnly, long>();
                using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var month = DateOnly.ParseExact(reader.GetString(0) + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        var amount = reader.GetInt64(2);
                        if (reader.GetInt64(1) == 1)
                        {
                            income[month] = income.GetValueOrDefault(month) + amount;
                        }
                        else
                        {
                            expense[month] = expense.GetValueOrDefault(month) - amount;
                        }
                    }
                }

                var months = ReportPeriod.Months(query.From, query.To)
                    .Select(m => new IncomeExpenseMonth(m, income.GetValueOrDefault(m), expense.GetValueOrDefault(m)))
                    .ToList();
                return new IncomeExpenseReport(await CurrencyAsync(db, ct).ConfigureAwait(false), query.From, query.To, months);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<NetWorthReport> GetNetWorthAsync(ReportQuery query, CancellationToken ct)
    {
        Validate(query);
        return RunAsync(
            async (db, connection) =>
            {
                var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
                var points = ReportPeriod.MonthEndPoints(query.From, query.To, today);
                var currency = await CurrencyAsync(db, ct).ConfigureAwait(false);
                if (points.Count == 0)
                {
                    return new NetWorthReport(currency, [], []);
                }

                var end = points[^1];
                var accounts = (await db.Accounts.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
                    .Where(a => query.IncludeTracking || a.IsOnBudget)
                    .Where(a => query.AccountIds is not { Count: > 0 } ids || ids.Contains(a.Id))
                    .OrderBy(a => AccountTypeInfo.GroupOf(a.Type, a.IsOnBudget)).ThenBy(a => a.SortOrder).ThenBy(a => a.Id)
                    .ToList();
                var snapshots = (await db.BalanceSnapshots.AsNoTracking().Where(s => s.Date <= end).ToListAsync(ct).ConfigureAwait(false))
                    .GroupBy(s => s.AccountId)
                    .ToDictionary(g => g.Key, g => g.Select(s => new ReportedBalance(s.Date, s.Balance)).ToList());

                // Monthly net per account; every row of a month is on or before that month's point.
                var monthly = new Dictionary<Guid, List<LedgerChange>>();
                using (var command = connection.CreateCommand())
                {
                    Add(command, "@end", Date(end));
                    command.CommandText = """
                        SELECT t."AccountId", substr(t."Date", 1, 7), SUM(t."Amount")
                        FROM "Transactions" AS t
                        WHERE t."IsDeleted" = 0 AND t."Date" <= @end
                        GROUP BY t."AccountId", substr(t."Date", 1, 7)
                        """;
                    await ReadChangesAsync(command, monthly, isMonth: true, ct).ConfigureAwait(false);
                }

                // Daily net for tracking accounts with snapshots (activity after a snapshot is added to it).
                var daily = new Dictionary<Guid, List<LedgerChange>>();
                using (var command = connection.CreateCommand())
                {
                    Add(command, "@end", Date(end));
                    command.CommandText = """
                        SELECT t."AccountId", t."Date", SUM(t."Amount")
                        FROM "Transactions" AS t
                        INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
                        WHERE t."IsDeleted" = 0 AND t."Date" <= @end AND a."IsOnBudget" = 0
                          AND EXISTS (SELECT 1 FROM "BalanceSnapshots" AS b WHERE b."AccountId" = t."AccountId")
                        GROUP BY t."AccountId", t."Date"
                        """;
                    await ReadChangesAsync(command, daily, isMonth: false, ct).ConfigureAwait(false);
                }

                var series = accounts.Select(a =>
                {
                    var usesSnapshots = !a.IsOnBudget && snapshots.ContainsKey(a.Id);
                    var balances = usesSnapshots
                        ? BalanceSeries.At(points, daily.GetValueOrDefault(a.Id) ?? [], snapshots[a.Id])
                        : BalanceSeries.At(points, monthly.GetValueOrDefault(a.Id) ?? [], []);
                    return new NetWorthAccount(a.Id, a.Name, a.Type, a.IsOnBudget, a.IsClosed, usesSnapshots, balances);
                }).ToList();

                var result = points.Select((date, i) => new NetWorthPoint(
                    date,
                    series.Where(s => !s.IsLiability).Sum(s => s.Balances[i]),
                    -series.Where(s => s.IsLiability).Sum(s => s.Balances[i]))).ToList();
                return new NetWorthReport(currency, result, series);
            },
            ct);
    }

    /// <summary>
    /// The rows a spending or income report counts, as a SELECT of ("Date", "CategoryId", "Amount"):
    /// parents without splits and split lines of non-deleted transactions in the range and the
    /// selected accounts; system rows (starting balances, reconciliation adjustments) never count;
    /// uncategorized transfers never count; categorized transfers count when transfers are included.
    /// </summary>
    internal static string RowSql(DbCommand command, ReportQuery query, string dateColumn)
    {
        Add(command, "@from", Date(query.From));
        Add(command, "@to", Date(query.To));
        Add(command, "@tracking", query.IncludeTracking ? 1 : 0);
        Add(command, "@transfers", query.IncludeTransfers ? 1 : 0);
        var accounts = new StringBuilder();
        if (query.AccountIds is { Count: > 0 } ids)
        {
            accounts.Append(" AND t.\"AccountId\" IN (");
            var i = 0;
            foreach (var id in ids.Distinct())
            {
                var name = "@account" + i.ToString(CultureInfo.InvariantCulture);
                Add(command, name, Key(id));
                accounts.Append(i++ == 0 ? string.Empty : ", ").Append(name);
            }

            accounts.Append(')');
        }

        var common = $"""
            t."IsDeleted" = 0 AND t."Source" <> '{nameof(TransactionSource.System)}'
              AND {dateColumn} >= @from AND {dateColumn} <= @to
              AND (@tracking = 1 OR a."IsOnBudget" = 1){accounts}
            """;
        return $"""
            SELECT t."Date" AS "Date", t."CategoryId" AS "CategoryId", t."Amount" AS "Amount"
            FROM "Transactions" AS t
            INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
            WHERE {common}
              AND (t."TransferAccountId" IS NULL OR (@transfers = 1 AND t."CategoryId" IS NOT NULL))
              AND NOT EXISTS (SELECT 1 FROM "TransactionSplits" AS sp WHERE sp."TransactionId" = t."Id")
            UNION ALL
            SELECT t."Date", s."CategoryId", s."Amount"
            FROM "TransactionSplits" AS s
            INNER JOIN "Transactions" AS t ON t."Id" = s."TransactionId"
            INNER JOIN "Accounts" AS a ON a."Id" = t."AccountId"
            WHERE {common}
              AND (s."TransferAccountId" IS NULL OR (@transfers = 1 AND s."CategoryId" IS NOT NULL))
            """;
    }

    private static async Task ReadChangesAsync(DbCommand command, Dictionary<Guid, List<LedgerChange>> into, bool isMonth, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var account = Guid.Parse(reader.GetString(0));
            var text = reader.GetString(1);
            var date = DateOnly.ParseExact(isMonth ? text + "-01" : text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!into.TryGetValue(account, out var list))
            {
                into[account] = list = [];
            }

            list.Add(new LedgerChange(date, reader.GetInt64(2)));
        }
    }

    private static void Validate(ReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.To < query.From)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(query));
        }
    }

    private async Task<T> RunAsync<T>(Func<KeelDbContext, DbConnection, Task<T>> work, CancellationToken ct) =>
        await Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                    return await work(db, db.Database.GetDbConnection()).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

    /// <summary>The budget's currency: that of the first on-budget account (single base currency, PRD D3).</summary>
    private static async Task<string> CurrencyAsync(KeelDbContext db, CancellationToken ct) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.IsOnBudget)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .Select(a => a.Currency)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? Currency.Default;

    private static string Key(Guid id) => RunningBalance.SortKey(id);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
