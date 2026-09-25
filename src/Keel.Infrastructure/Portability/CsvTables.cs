using System.Globalization;
using CsvHelper;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Portability;

/// <summary>Names of the small reference tables, loaded once per export.</summary>
internal sealed class Lookups
{
    public required IReadOnlyDictionary<Guid, (string Name, string Currency)> Accounts { get; init; }

    public required IReadOnlyDictionary<Guid, (string Group, string Name)> Categories { get; init; }

    public required IReadOnlyDictionary<Guid, string> Payees { get; init; }

    public required IReadOnlyDictionary<Guid, string> Tags { get; init; }

    /// <summary>The budget's currency: the most common currency of on-budget accounts (default USD).</summary>
    public required string BudgetCurrency { get; init; }

    public static async Task<Lookups> LoadAsync(KeelDbContext db, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Select(a => new { a.Id, a.Name, a.Currency, a.IsOnBudget }).ToListAsync(ct).ConfigureAwait(false);
        var groups = await db.CategoryGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, ct).ConfigureAwait(false);
        var categories = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.GroupId, c.Name }).ToListAsync(ct).ConfigureAwait(false);
        return new Lookups
        {
            Accounts = accounts.ToDictionary(a => a.Id, a => (a.Name, a.Currency)),
            Categories = categories.ToDictionary(c => c.Id, c => (groups.GetValueOrDefault(c.GroupId) ?? string.Empty, c.Name)),
            Payees = await db.Payees.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct).ConfigureAwait(false),
            Tags = await db.Tags.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct).ConfigureAwait(false),
            BudgetCurrency = accounts.Where(a => a.IsOnBudget).GroupBy(a => a.Currency).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault() ?? Currency.Default,
        };
    }

    public string Account(Guid? id) => id is { } a && Accounts.TryGetValue(a, out var account) ? account.Name : string.Empty;

    public string CurrencyOf(Guid? account) => account is { } a && Accounts.TryGetValue(a, out var found) ? found.Currency : BudgetCurrency;

    public (string Group, string Name) Category(Guid? id) => id is { } c && Categories.TryGetValue(c, out var category) ? category : (string.Empty, string.Empty);

    public string Payee(Guid? id) => id is { } p && Payees.TryGetValue(p, out var name) ? name : string.Empty;
}

/// <summary>
/// The CSV files of the export (F-REP-6): stable column order, ISO dates (months as <c>yyyy-MM</c>), amounts
/// as invariant decimals with the currency's minor digits, booleans as <c>true</c>/<c>false</c>, ids in
/// lower-case "D" form. Each method writes the header and its rows and returns the row count.
/// </summary>
internal static class CsvTables
{
    public static readonly string[] TransactionColumns =
        ["Id", "Parent Id", "Date", "Account", "Payee", "Category Group", "Category", "Transfer Account", "Memo", "Amount", "Currency", "Status", "Approved", "Source", "Tags", "Import Id"];

    public static readonly string[] BudgetColumns = ["Month", "Category Group", "Category", "Category Id", "Assigned", "Currency"];

    public static readonly string[] AccountColumns =
        ["Id", "Name", "Type", "On Budget", "Closed", "Currency", "Opening Date", "Balance", "Cleared Balance", "Sort Order", "Notes"];

    public static readonly string[] CategoryColumns =
        ["Group Id", "Group", "Group Sort Order", "Group Hidden", "Group System", "Id", "Category", "Sort Order", "Hidden", "System", "Linked Account", "Flex Kind", "Notes"];

    public static readonly string[] PayeeColumns = ["Id", "Name", "Default Category Group", "Default Category", "Transfer Payee For"];

    public static readonly string[] RuleColumns = ["Id", "Sort Order", "Name", "Enabled", "Continue After Match", "Conditions", "Actions"];

    public static readonly string[] TargetColumns = ["Category Id", "Category Group", "Category", "Type", "Amount", "Currency", "Target Date", "Cadence", "Linked Account"];

    public static readonly string[] ScheduledColumns =
        ["Id", "Account", "Payee", "Category Group", "Category", "Transfer Account", "Memo", "Amount", "Currency", "Recurrence Rule", "Next Date", "End Date", "Auto Enter"];

    public static readonly string[] RecurringColumns =
    [
        "Id", "Payee", "Account", "Cadence", "Expected Amount", "Amount Tolerance", "Currency", "Variable Amount", "Next Expected Date",
        "Last Seen Date", "Confidence", "Status", "Subscription", "Category Group", "Category", "Scheduled Transaction Id",
    ];

    // Transactions in ledger order (Date, Id), paged by key; a split transaction is one line per split with
    // the parent's id in "Parent Id" (its lines add up to the transaction). Deleted rows are left out.
    public static async Task<int> TransactionsAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, int pageSize, CancellationToken ct)
    {
        Header(csv, TransactionColumns);
        const string Sql = """
            SELECT * FROM "Transactions"
            WHERE "IsDeleted" = 0 AND ("Date" > {0} OR ("Date" = {0} AND "Id" > {1}))
            ORDER BY "Date", "Id"
            LIMIT {2}
            """;
        var lastDate = "0000-00-00";
        var lastId = string.Empty;
        var count = 0;
        while (true)
        {
            var page = (await db.Transactions.FromSqlRaw(Sql, lastDate, lastId, pageSize).IgnoreQueryFilters().AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
                .OrderBy(t => t.Date).ThenBy(t => RunningKey(t.Id), StringComparer.Ordinal)
                .ToList();
            if (page.Count == 0)
            {
                break;
            }

            var ids = page.Select(t => t.Id).ToList();
            var splits = (await db.TransactionSplits.IgnoreQueryFilters().AsNoTracking().Where(s => ids.Contains(s.TransactionId)).ToListAsync(ct).ConfigureAwait(false))
                .GroupBy(s => s.TransactionId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => RunningKey(s.Id), StringComparer.Ordinal).ToList());
            var tags = (await db.TransactionTags.IgnoreQueryFilters().AsNoTracking().Where(t => ids.Contains(t.TransactionId)).ToListAsync(ct).ConfigureAwait(false))
                .GroupBy(t => t.TransactionId)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(t => lookups.Tags.GetValueOrDefault(t.TagId) ?? string.Empty).Where(n => n.Length > 0).Order(StringComparer.CurrentCultureIgnoreCase)));
            foreach (var t in page)
            {
                var currency = lookups.CurrencyOf(t.AccountId);
                var tagText = tags.GetValueOrDefault(t.Id) ?? string.Empty;
                if (splits.TryGetValue(t.Id, out var lines))
                {
                    foreach (var line in lines)
                    {
                        var (group, category) = lookups.Category(line.CategoryId);
                        Row(csv, Id(line.Id), Id(t.Id), Date(t.Date), lookups.Account(t.AccountId), lookups.Payee(t.PayeeId), group, category,
                            lookups.Account(line.TransferAccountId), line.Memo ?? t.Memo, Amount(line.Amount, currency), currency, t.Status.ToString(),
                            Bool(t.IsApproved), t.Source.ToString(), tagText, t.ProviderTransactionId);
                        count++;
                    }
                }
                else
                {
                    var (group, category) = lookups.Category(t.CategoryId);
                    Row(csv, Id(t.Id), null, Date(t.Date), lookups.Account(t.AccountId), lookups.Payee(t.PayeeId), group, category,
                        lookups.Account(t.TransferAccountId), t.Memo, Amount(t.Amount, currency), currency, t.Status.ToString(),
                        Bool(t.IsApproved), t.Source.ToString(), tagText, t.ProviderTransactionId);
                    count++;
                }
            }

            await csv.FlushAsync().ConfigureAwait(false);
            if (page.Count < pageSize)
            {
                break;
            }

            lastDate = Date(page[^1].Date);
            lastId = RunningKey(page[^1].Id);
        }

        return count;
    }

    public static async Task<int> BudgetAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, CancellationToken ct)
    {
        Header(csv, BudgetColumns);
        var query = from b in db.BudgetAssignments.AsNoTracking()
                    join c in db.Categories on b.CategoryId equals c.Id
                    join g in db.CategoryGroups on c.GroupId equals g.Id
                    orderby b.Month, g.SortOrder, g.Name, c.SortOrder, c.Name
                    select new { b.Month, Group = g.Name, Category = c.Name, b.CategoryId, b.Assigned };
        var count = 0;
        await foreach (var b in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            Row(csv, b.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture), b.Group, b.Category, Id(b.CategoryId), Amount(b.Assigned, lookups.BudgetCurrency), lookups.BudgetCurrency);
            count++;
        }

        return count;
    }

    public static async Task<int> AccountsAsync(CsvWriter csv, KeelDbContext db, CancellationToken ct)
    {
        Header(csv, AccountColumns);
        var balances = await db.Transactions.GroupBy(t => t.AccountId)
            .Select(g => new { g.Key, Balance = g.Sum(t => t.Amount), Cleared = g.Where(t => t.Status != TransactionStatus.Uncleared).Sum(t => t.Amount) })
            .ToDictionaryAsync(b => b.Key, ct).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        foreach (var a in accounts.OrderBy(a => a.Group).ThenBy(a => a.SortOrder).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var balance = balances.GetValueOrDefault(a.Id);
            Row(csv, Id(a.Id), a.Name, a.Type.ToString(), Bool(a.IsOnBudget), Bool(a.IsClosed), a.Currency, Date(a.OpeningDate),
                Amount(balance?.Balance ?? 0, a.Currency), Amount(balance?.Cleared ?? 0, a.Currency), Int(a.SortOrder), a.Notes);
        }

        return accounts.Count;
    }

    public static async Task<int> CategoriesAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, CancellationToken ct)
    {
        Header(csv, CategoryColumns);
        var groups = await db.CategoryGroups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct).ConfigureAwait(false);
        var categories = (await db.Categories.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).ToLookup(c => c.GroupId);
        var count = 0;
        foreach (var g in groups)
        {
            var inGroup = categories[g.Id].OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (inGroup.Count == 0)
            {
                Row(csv, Id(g.Id), g.Name, Int(g.SortOrder), Bool(g.IsHidden), Bool(g.IsSystem), null, null, null, null, null, null, null, null);
                count++;
            }

            foreach (var c in inGroup)
            {
                Row(csv, Id(g.Id), g.Name, Int(g.SortOrder), Bool(g.IsHidden), Bool(g.IsSystem), Id(c.Id), c.Name, Int(c.SortOrder), Bool(c.IsHidden),
                    Bool(c.IsSystem), lookups.Account(c.LinkedAccountId), c.FlexKind.ToString(), c.Notes);
                count++;
            }
        }

        return count;
    }

    public static async Task<int> PayeesAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, CancellationToken ct)
    {
        Header(csv, PayeeColumns);
        var count = 0;
        await foreach (var p in db.Payees.AsNoTracking().OrderBy(p => p.NormalizedName).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            var (group, category) = lookups.Category(p.DefaultCategoryId);
            Row(csv, Id(p.Id), p.Name, group, category, lookups.Account(p.IsTransferPayeeForAccountId));
            count++;
        }

        return count;
    }

    public static async Task<int> RulesAsync(CsvWriter csv, KeelDbContext db, CancellationToken ct)
    {
        Header(csv, RuleColumns);
        var rules = await db.Rules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
        foreach (var r in rules)
        {
            Row(csv, Id(r.Id), Int(r.SortOrder), r.Name, Bool(r.IsEnabled), Bool(r.ContinueAfterMatch), r.ConditionsJson, r.ActionsJson);
        }

        return rules.Count;
    }

    public static async Task<int> TargetsAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, CancellationToken ct)
    {
        Header(csv, TargetColumns);
        var targets = await db.Targets.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in targets.OrderBy(t => lookups.Category(t.CategoryId).Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => lookups.Category(t.CategoryId).Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var (group, category) = lookups.Category(t.CategoryId);
            Row(csv, Id(t.CategoryId), group, category, t.Type.ToString(), Amount(t.Amount, lookups.BudgetCurrency), lookups.BudgetCurrency,
                t.TargetDate is { } d ? Date(d) : null, t.Cadence?.ToString(), lookups.Account(t.LinkedAccountId));
        }

        return targets.Count;
    }

    public static async Task<int> ScheduledAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, CancellationToken ct)
    {
        Header(csv, ScheduledColumns);
        var schedules = await db.ScheduledTransactions.AsNoTracking().OrderBy(s => s.NextDate).ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in schedules)
        {
            var (group, category) = lookups.Category(s.CategoryId);
            var currency = lookups.CurrencyOf(s.AccountId);
            Row(csv, Id(s.Id), lookups.Account(s.AccountId), lookups.Payee(s.PayeeId), group, category, lookups.Account(s.TransferAccountId), s.Memo,
                Amount(s.Amount, currency), currency, s.RecurrenceRule, Date(s.NextDate), s.EndDate is { } end ? Date(end) : null, Bool(s.AutoEnter));
        }

        return schedules.Count;
    }

    public static async Task<int> RecurringAsync(CsvWriter csv, KeelDbContext db, Lookups lookups, CancellationToken ct)
    {
        Header(csv, RecurringColumns);
        var items = await db.RecurringItems.AsNoTracking().OrderBy(r => r.NextExpectedDate).ToListAsync(ct).ConfigureAwait(false);
        foreach (var r in items)
        {
            var (group, category) = lookups.Category(r.CategoryId);
            var currency = lookups.CurrencyOf(r.AccountId);
            Row(csv, Id(r.Id), lookups.Payee(r.PayeeId), lookups.Account(r.AccountId), r.Cadence.ToString(), Amount(r.ExpectedAmount, currency),
                Amount(r.AmountTolerance, currency), currency, Bool(r.IsVariableAmount), Date(r.NextExpectedDate), Date(r.LastSeenDate),
                r.Confidence.ToString("0.####", CultureInfo.InvariantCulture), r.Status.ToString(), Bool(r.IsSubscription), group, category,
                r.ScheduledTransactionId is { } s ? Id(s) : null);
        }

        return items.Count;
    }

    /// <summary>Minor units as an invariant decimal with the currency's digits ("-12.30", JPY "-1230").</summary>
    public static string Amount(long minor, string currency)
    {
        var digits = Currency.MinorUnitDigits(currency);
        var value = (decimal)minor / Currency.MinorUnitsPerMajor(currency);
        return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static string RunningKey(Guid id) => Keel.Domain.Ledger.RunningBalance.SortKey(id);

    private static string Id(Guid id) => id.ToString("D");

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Header(CsvWriter csv, string[] columns) => Row(csv, columns);

    private static void Row(CsvWriter csv, params string?[] fields)
    {
        foreach (var field in fields)
        {
            csv.WriteField(field ?? string.Empty);
        }

        csv.NextRecord();
    }
}
