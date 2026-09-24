using System.Data.Common;
using System.Globalization;
using System.Text;
using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>
/// The paged register (F-ACC-2) in raw SQL over the covering register indexes. A page is read in
/// three bounded queries: the rows (filter, sort, LIMIT/OFFSET), their running balances (a window
/// sum over the ledger between the page's first and last row plus one prefix sum), and their splits.
/// </summary>
public sealed class RegisterQuery(IDbContextFactory<KeelDbContext> factory) : IRegisterQuery
{
    private const string From = """
        FROM "Transactions" AS t
        LEFT JOIN "Accounts" AS a ON a."Id" = t."AccountId"
        LEFT JOIN "Payees" AS p ON p."Id" = t."PayeeId"
        LEFT JOIN "Categories" AS c ON c."Id" = t."CategoryId"
        LEFT JOIN "Accounts" AS ta ON ta."Id" = t."TransferAccountId"
        """;

    /// <inheritdoc />
    public Task<int> CountAsync(RegisterFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return RunAsync(
            async (connection, currency) =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(command, filter, currency);
                command.CommandText = $"SELECT COUNT(*) {From} WHERE {where}";
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            },
            filter.AccountId,
            ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RegisterRow>> GetPageAsync(RegisterFilter filter, RegisterSort sort, int skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        return RunAsync(
            async (connection, currency) =>
            {
                var rows = await ReadRowsAsync(connection, filter, sort, skip, take, currency, ct).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return (IReadOnlyList<RegisterRow>)[];
                }

                var balances = await RunningBalancesAsync(connection, filter.AccountId, rows, ct).ConfigureAwait(false);
                var splits = await SplitsAsync(connection, rows, ct).ConfigureAwait(false);
                IReadOnlyList<RegisterRow> page = rows
                    .Select(r => r with
                    {
                        RunningBalance = balances.GetValueOrDefault(r.Id),
                        Splits = splits.TryGetValue(r.Id, out var lines) ? lines : [],
                    })
                    .ToList();
                return page;
            },
            filter.AccountId,
            ct);
    }

    /// <inheritdoc />
    public Task<int> IndexOfAsync(RegisterFilter filter, RegisterSort sort, Guid transactionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(sort);
        return RunAsync(
            async (connection, currency) =>
            {
                using var command = connection.CreateCommand();
                var where = BuildWhere(command, filter, currency);
                Add(command, "@target", Key(transactionId));
                command.CommandText = $"""
                    SELECT n FROM (
                      SELECT t."Id" AS id, ROW_NUMBER() OVER (ORDER BY {OrderBy(sort)}) - 1 AS n
                      {From}
                      WHERE {where}
                    ) WHERE id = @target
                    """;
                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null or DBNull ? -1 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
            },
            filter.AccountId,
            ct);
    }

    /// <inheritdoc />
    public Task<RegisterSummary> GetSummaryAsync(Guid? accountId, CancellationToken ct) => RunAsync(
        async (connection, currency) =>
        {
            using var command = connection.CreateCommand();
            var accountFilter = string.Empty;
            if (accountId is { } id)
            {
                Add(command, "@account", Key(id));
                accountFilter = """AND "AccountId" = @account""";
            }

            command.CommandText = $"""
                SELECT COALESCE(SUM("Amount"), 0),
                       COALESCE(SUM(CASE WHEN "Status" <> 'Uncleared' THEN "Amount" END), 0),
                       COUNT(*),
                       COALESCE(SUM(CASE WHEN "IsApproved" = 0 THEN 1 END), 0)
                FROM "Transactions" WHERE "IsDeleted" = 0 {accountFilter}
                """;
            long ledger, cleared;
            int count, unapproved;
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                await reader.ReadAsync(ct).ConfigureAwait(false);
                ledger = reader.GetInt64(0);
                cleared = reader.GetInt64(1);
                count = reader.GetInt32(2);
                unapproved = reader.GetInt32(3);
            }

            long? reported = null;
            DateTime? reportedAt = null;
            if (accountId is not null)
            {
                command.Parameters.Clear();
                Add(command, "@account", Key(accountId.Value));
                command.CommandText = """SELECT "ReportedBalance", "ReportedBalanceAt" FROM "Accounts" WHERE "Id" = @account""";
                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    reported = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                    reportedAt = reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                }
            }

            return new RegisterSummary(currency, ledger, cleared, reported, reportedAt, count, unapproved);
        },
        accountId,
        ct);

    private async Task<T> RunAsync<T>(Func<DbConnection, string, Task<T>> work, Guid? accountId, CancellationToken ct) =>
        await Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                    var connection = db.Database.GetDbConnection();
                    var currency = await CurrencyAsync(connection, accountId, ct).ConfigureAwait(false);
                    return await work(connection, currency).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

    // The account's currency, or the most common one for the All Accounts register (v1 is single-currency, D3).
    private static async Task<string> CurrencyAsync(DbConnection connection, Guid? accountId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        if (accountId is { } id)
        {
            Add(command, "@account", Key(id));
            command.CommandText = """SELECT "Currency" FROM "Accounts" WHERE "Id" = @account""";
        }
        else
        {
            command.CommandText = """SELECT "Currency" FROM "Accounts" GROUP BY "Currency" ORDER BY COUNT(*) DESC LIMIT 1""";
        }

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? Currency.Default;
    }

    private static async Task<List<RegisterRow>> ReadRowsAsync(DbConnection connection, RegisterFilter filter, RegisterSort sort, int skip, int take, string currency, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, filter, currency);
        Add(command, "@take", take);
        Add(command, "@skip", skip);
        command.CommandText = $"""
            SELECT t."Id", t."AccountId", a."Name", a."Currency", t."Date", t."PayeeId", COALESCE(p."Name", t."PayeeRaw"),
                   t."CategoryId", c."Name", t."Memo", t."Amount", t."Status", t."IsApproved", t."Source",
                   t."TransferAccountId", ta."Name", t."TransferPairId"
            {From}
            WHERE {where}
            ORDER BY {OrderBy(sort)}
            LIMIT @take OFFSET @skip
            """;

        var rows = new List<RegisterRow>(take);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RegisterRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? currency : reader.GetString(3),
                DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10),
                Enum.Parse<TransactionStatus>(reader.GetString(11)),
                reader.GetBoolean(12),
                Enum.Parse<TransactionSource>(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetGuid(16),
                0,
                []));
        }

        return rows;
    }

    // Running balance of each page row in ledger order (Date, Id) over the whole account (or all
    // accounts): the prefix sum before the page's earliest row plus a window sum up to its latest.
    private static async Task<Dictionary<Guid, long>> RunningBalancesAsync(DbConnection connection, Guid? accountId, IReadOnlyList<RegisterRow> rows, CancellationToken ct)
    {
        var first = rows[0];
        var last = rows[0];
        foreach (var row in rows)
        {
            if (RunningBalance.CompareLedgerOrder(row.Date, row.Id, first.Date, first.Id) < 0)
            {
                first = row;
            }

            if (RunningBalance.CompareLedgerOrder(row.Date, row.Id, last.Date, last.Id) > 0)
            {
                last = row;
            }
        }

        using var command = connection.CreateCommand();
        var accountFilter = string.Empty;
        if (accountId is { } id)
        {
            Add(command, "@account", Key(id));
            accountFilter = """AND "AccountId" = @account""";
        }

        Add(command, "@firstDate", Date(first.Date));
        Add(command, "@firstId", Key(first.Id));
        Add(command, "@lastDate", Date(last.Date));
        Add(command, "@lastId", Key(last.Id));
        command.CommandText = $"""
            SELECT COALESCE(SUM("Amount"), 0) FROM "Transactions"
            WHERE "IsDeleted" = 0 {accountFilter} AND ("Date", "Id") < (@firstDate, @firstId)
            """;
        var prefix = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

        var wanted = rows.Select(r => r.Id).ToHashSet();
        command.CommandText = $"""
            SELECT "Id", SUM("Amount") OVER (ORDER BY "Date", "Id" ROWS UNBOUNDED PRECEDING)
            FROM "Transactions"
            WHERE "IsDeleted" = 0 {accountFilter}
              AND ("Date", "Id") >= (@firstDate, @firstId) AND ("Date", "Id") <= (@lastDate, @lastId)
            ORDER BY "Date", "Id"
            """;
        var result = new Dictionary<Guid, long>(rows.Count);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var rowId = reader.GetGuid(0);
            if (wanted.Contains(rowId))
            {
                result[rowId] = checked(prefix + reader.GetInt64(1));
            }
        }

        return result;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<RegisterSplit>>> SplitsAsync(DbConnection connection, IReadOnlyList<RegisterRow> rows, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        var names = new List<string>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            names.Add("@p" + i.ToString(CultureInfo.InvariantCulture));
            Add(command, names[^1], Key(rows[i].Id));
        }

        command.CommandText = $"""
            SELECT s."TransactionId", s."Id", s."CategoryId", c."Name", s."Memo", s."Amount"
            FROM "TransactionSplits" AS s LEFT JOIN "Categories" AS c ON c."Id" = s."CategoryId"
            WHERE s."TransactionId" IN ({string.Join(", ", names)})
            ORDER BY s."TransactionId", s.rowid
            """;
        var result = new Dictionary<Guid, List<RegisterSplit>>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var parent = reader.GetGuid(0);
            if (!result.TryGetValue(parent, out var lines))
            {
                result[parent] = lines = [];
            }

            lines.Add(new RegisterSplit(
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<RegisterSplit>)kv.Value);
    }

    private static string OrderBy(RegisterSort sort)
    {
        var dir = sort.Descending ? "DESC" : "ASC";
        var ledger = $"""t."Date" {dir}, t."Id" {dir}""";
        return sort.Column switch
        {
            RegisterSortColumn.Account => $"""a."Name" COLLATE NOCASE {dir}, {ledger}""",
            RegisterSortColumn.Payee => $"""COALESCE(ta."Name", p."Name", t."PayeeRaw") COLLATE NOCASE {dir}, {ledger}""",
            RegisterSortColumn.Category => $"""c."Name" COLLATE NOCASE {dir}, {ledger}""",
            RegisterSortColumn.Memo => $"""t."Memo" COLLATE NOCASE {dir}, {ledger}""",
            RegisterSortColumn.Amount => $"""t."Amount" {dir}, {ledger}""",
            RegisterSortColumn.Status => $"""t."Status" {dir}, {ledger}""",
            _ => ledger,
        };
    }

    private static string BuildWhere(DbCommand command, RegisterFilter filter, string currency)
    {
        var where = new StringBuilder("""t."IsDeleted" = 0""");
        if (filter.AccountId is { } accountId)
        {
            Add(command, "@account", Key(accountId));
            where.Append(""" AND t."AccountId" = @account""");
        }

        var search = SearchQuery.Parse(filter.Search);
        var from = Max(filter.From, search.DateFrom);
        var to = Min(filter.To, search.DateTo);
        if (from is { } f)
        {
            Add(command, "@from", Date(f));
            where.Append(""" AND t."Date" >= @from""");
        }

        if (to is { } tDate)
        {
            Add(command, "@to", Date(tDate));
            where.Append(""" AND t."Date" <= @to""");
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var names = statuses.Distinct().Select((s, i) =>
            {
                var name = "@status" + i.ToString(CultureInfo.InvariantCulture);
                Add(command, name, s.ToString());
                return name;
            });
            where.Append(CultureInfo.InvariantCulture, $""" AND t."Status" IN ({string.Join(", ", names)})""");
        }

        if (filter.CategoryId is { } categoryId)
        {
            Add(command, "@category", Key(categoryId));
            where.Append("""
                 AND (t."CategoryId" = @category OR EXISTS (SELECT 1 FROM "TransactionSplits" AS s WHERE s."TransactionId" = t."Id" AND s."CategoryId" = @category))
                """);
        }

        if (filter.UnapprovedOnly)
        {
            where.Append(""" AND t."IsApproved" = 0""");
        }

        var counter = 0;
        string Like(string value)
        {
            var name = "@q" + counter++.ToString(CultureInfo.InvariantCulture);
            Add(command, name, "%" + PayeeService.EscapeLike(value) + "%");
            return name;
        }

        var perUnit = Currency.MinorUnitsPerMajor(currency);
        foreach (var term in search.Terms)
        {
            var q = Like(term);
            var clause = new StringBuilder($"""
                p."Name" LIKE {q} ESCAPE '\' OR t."PayeeRaw" LIKE {q} ESCAPE '\' OR t."Memo" LIKE {q} ESCAPE '\'
                OR c."Name" LIKE {q} ESCAPE '\' OR a."Name" LIKE {q} ESCAPE '\' OR ta."Name" LIKE {q} ESCAPE '\'
                OR EXISTS (SELECT 1 FROM "TransactionSplits" AS s LEFT JOIN "Categories" AS sc ON sc."Id" = s."CategoryId"
                           WHERE s."TransactionId" = t."Id" AND (s."Memo" LIKE {q} ESCAPE '\' OR sc."Name" LIKE {q} ESCAPE '\'))
                """);
            if (decimal.TryParse(term.TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                var name = "@n" + counter++.ToString(CultureInfo.InvariantCulture);
                Add(command, name, ToMinor(number, perUnit));
                clause.Append(CultureInfo.InvariantCulture, $""" OR ABS(t."Amount") = {name}""");
            }

            where.Append(CultureInfo.InvariantCulture, $" AND ({clause})");
        }

        AppendAny(where, search.Payees, q => $"""COALESCE(p."Name", t."PayeeRaw") LIKE {q} ESCAPE '\' OR ta."Name" LIKE {q} ESCAPE '\'""", Like);
        AppendAny(where, search.Memos, q => $"""t."Memo" LIKE {q} ESCAPE '\' OR EXISTS (SELECT 1 FROM "TransactionSplits" AS s WHERE s."TransactionId" = t."Id" AND s."Memo" LIKE {q} ESCAPE '\')""", Like);
        AppendAny(where, search.Categories, q => $"""c."Name" LIKE {q} ESCAPE '\' OR EXISTS (SELECT 1 FROM "TransactionSplits" AS s JOIN "Categories" AS sc ON sc."Id" = s."CategoryId" WHERE s."TransactionId" = t."Id" AND sc."Name" LIKE {q} ESCAPE '\')""", Like);
        AppendAny(where, search.Accounts, q => $"""a."Name" LIKE {q} ESCAPE '\'""", Like);
        AppendAny(where, search.Tags, q => $"""EXISTS (SELECT 1 FROM "TransactionTags" AS tt JOIN "Tags" AS g ON g."Id" = tt."TagId" WHERE tt."TransactionId" = t."Id" AND g."Name" LIKE {q} ESCAPE '\')""", Like);

        if (search.AmountMin is { } min)
        {
            Add(command, "@amountMin", ToMinor(min, perUnit));
            where.Append(search.AmountMinInclusive ? """ AND ABS(t."Amount") >= @amountMin""" : """ AND ABS(t."Amount") > @amountMin""");
        }

        if (search.AmountMax is { } max)
        {
            Add(command, "@amountMax", ToMinor(max, perUnit));
            where.Append(search.AmountMaxInclusive ? """ AND ABS(t."Amount") <= @amountMax""" : """ AND ABS(t."Amount") < @amountMax""");
        }

        return where.ToString();
    }

    private static void AppendAny(StringBuilder where, IReadOnlyList<string> values, Func<string, string> clause, Func<string, string> like)
    {
        if (values.Count == 0)
        {
            return;
        }

        where.Append(" AND (")
            .AppendJoin(" OR ", values.Select(v => "(" + clause(like(v)) + ")"))
            .Append(')');
    }

    private static long ToMinor(decimal major, long perUnit) => decimal.ToInt64(decimal.Round(major * perUnit, 0, MidpointRounding.ToEven));

    private static DateOnly? Max(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    private static DateOnly? Min(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : (a < b ? a : b);

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
