using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Fixtures;

/// <summary>Options for <see cref="LedgerFixtureGenerator"/>.</summary>
/// <param name="TransactionCount">Exact number of transactions to write (splits are extra rows).</param>
/// <param name="Seed">Random seed; the same seed always yields the same ledger, ids included.</param>
/// <param name="EndDate">Date of the newest transaction; null uses 2026-08-31.</param>
public sealed record LedgerFixtureOptions(int TransactionCount = LedgerFixtureOptions.DefaultCount, int Seed = 42, DateOnly? EndDate = null)
{
    /// <summary>The performance fixture size (PRD 11: register open &lt; 500 ms at 100k rows).</summary>
    public const int DefaultCount = 100_000;
}

/// <summary>What the generator wrote.</summary>
/// <param name="Accounts">Account ids by name, in sidebar order.</param>
/// <param name="Categories">User category ids by name (40).</param>
/// <param name="TransactionCount">Transactions written.</param>
/// <param name="SplitCount">Split lines written.</param>
/// <param name="FirstDate">Oldest transaction date.</param>
/// <param name="LastDate">Newest transaction date.</param>
public sealed record LedgerFixture(
    IReadOnlyDictionary<string, Guid> Accounts,
    IReadOnlyDictionary<string, Guid> Categories,
    int TransactionCount,
    int SplitCount,
    DateOnly FirstDate,
    DateOnly LastDate);

/// <summary>
/// Writes a deterministic, realistic ledger into an empty, migrated budget file: 8 accounts
/// (cash, credit and tracking), 40 categories in 8 groups, about 90 payees, biweekly paychecks,
/// monthly bills, card payments and transfers (including on-budget to tracking), splits, balance
/// snapshots, and day-to-day spending up to the requested transaction count. Used by tests,
/// benchmarks and screenshots. Bulk rows are inserted with prepared SQL in one transaction.
/// </summary>
public static class LedgerFixtureGenerator
{
    private static readonly (string Group, string[] Categories)[] CategoryTree =
    [
        ("Bills", ["Rent/Mortgage", "Electric", "Water", "Internet", "Phone", "Insurance", "Streaming"]),
        ("Everyday", ["Groceries", "Dining Out", "Coffee", "Fuel", "Transit", "Household", "Personal Care"]),
        ("Health", ["Pharmacy", "Doctor", "Dental", "Fitness"]),
        ("Kids & Pets", ["Childcare", "School", "Pet Food", "Vet"]),
        ("Shopping", ["Clothing", "Electronics", "Home Improvement", "Books", "Hobbies"]),
        ("Fun", ["Entertainment", "Travel", "Gifts", "Concerts"]),
        ("Savings Goals", ["Emergency Fund", "Vacation Fund", "New Car", "Investing"]),
        ("Giving & Misc", ["Charity", "Subscriptions", "Bank Fees", "Taxes", "Miscellaneous"]),
    ];

    // Spending categories: weight, amount range in dollars, payees.
    private static readonly (string Category, int Weight, int Min, int Max, string[] Payees)[] Spending =
    [
        ("Groceries", 180, 12, 180, ["Trader Joe's", "Whole Foods Market", "Safeway", "Costco", "Kroger", "Aldi"]),
        ("Dining Out", 140, 9, 95, ["Chipotle", "Olive Garden", "Panera Bread", "Local Taqueria", "Sushi House", "Pizza Palace"]),
        ("Coffee", 100, 3, 14, ["Starbucks", "Blue Bottle Coffee", "Peet's Coffee"]),
        ("Fuel", 70, 25, 85, ["Shell", "Chevron", "Exxon"]),
        ("Transit", 55, 2, 45, ["Metro Transit", "Uber", "Lyft"]),
        ("Household", 70, 8, 160, ["Target", "Walmart", "IKEA", "HomeGoods"]),
        ("Personal Care", 30, 10, 90, ["Great Clips", "Sephora", "Ulta Beauty"]),
        ("Pharmacy", 30, 5, 70, ["CVS Pharmacy", "Walgreens"]),
        ("Doctor", 8, 25, 250, ["Family Health Clinic"]),
        ("Dental", 4, 40, 300, ["Bright Smile Dental"]),
        ("School", 6, 10, 120, ["Lincoln Elementary PTA", "Scholastic Books"]),
        ("Pet Food", 20, 15, 80, ["Petco", "Chewy"]),
        ("Vet", 3, 60, 400, ["Happy Paws Vet"]),
        ("Clothing", 30, 15, 180, ["Old Navy", "Uniqlo", "Nordstrom"]),
        ("Electronics", 8, 20, 900, ["Best Buy", "Apple Store"]),
        ("Home Improvement", 16, 10, 400, ["Home Depot", "Lowe's"]),
        ("Books", 14, 8, 60, ["Barnes & Noble", "Powell's Books"]),
        ("Hobbies", 16, 10, 150, ["Michaels", "REI"]),
        ("Entertainment", 25, 8, 90, ["AMC Theatres", "Steam Games", "Bowling Alley"]),
        ("Travel", 6, 80, 1200, ["Delta Air Lines", "Marriott", "Airbnb"]),
        ("Gifts", 12, 15, 150, ["Etsy", "Amazon"]),
        ("Concerts", 4, 40, 250, ["Ticketmaster"]),
        ("Charity", 6, 10, 100, ["Red Cross", "Local Food Bank"]),
        ("Miscellaneous", 15, 3, 60, ["Post Office", "Parking Meter", "Farmers Market"]),
        ("Bank Fees", 2, 3, 35, ["Monthly Service Fee"]),
    ];

    private static readonly string[] Memos =
    [
        "weekly shop", "lunch with team", "birthday", "refill", "gift for mom", "weekend", "work trip",
        "kids", "split with roommate", "returns pending", "snacks", "date night",
    ];

    private static readonly (string Name, AccountType Type, long Opening)[] AccountSpecs =
    [
        ("Everyday Checking", AccountType.Checking, 2_500_00),
        ("Bills Checking", AccountType.Checking, 1_200_00),
        ("Emergency Savings", AccountType.Savings, 8_000_00),
        ("Wallet", AccountType.Cash, 120_00),
        ("Visa Rewards", AccountType.CreditCard, -850_00),
        ("Travel Mastercard", AccountType.CreditCard, -300_00),
        ("Home Mortgage", AccountType.Loan, -320_000_00),
        ("Brokerage", AccountType.Investment, 45_000_00),
    ];

    /// <summary>Generates the fixture into the (empty, migrated) budget file behind <paramref name="factory"/>.</summary>
    public static async Task<LedgerFixture> GenerateAsync(IDbContextFactory<KeelDbContext> factory, LedgerFixtureOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TransactionCount, AccountSpecs.Length);

        var random = new Random(options.Seed);
        var end = options.EndDate ?? new DateOnly(2026, 8, 31);
        var days = Math.Max(90, (int)Math.Ceiling(options.TransactionCount / 55.0));
        var start = end.AddDays(-days + 1);
        var ids = new IdSource(random);
        var createdAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        // Reference data through EF: accounts, groups, categories, payees, snapshots.
        var accounts = AccountSpecs.Select((spec, i) =>
        {
            var account = Account.Create(spec.Name, spec.Type, start);
            account.Id = ids.Next(start, i);
            account.SortOrder = i;
            account.OwnerProfileId = SystemIds.DefaultProfile;
            return account;
        }).ToList();
        var byName = accounts.ToDictionary(a => a.Name, StringComparer.Ordinal);

        var groups = new List<CategoryGroup>();
        var categories = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach (var (groupName, names) in CategoryTree)
        {
            var group = new CategoryGroup { Id = ids.Next(start, 100 + groups.Count), Name = groupName, SortOrder = groups.Count + 2 };
            groups.Add(group);
            for (var i = 0; i < names.Length; i++)
            {
                categories[names[i]] = new Category { Id = ids.Next(start, 200 + categories.Count), GroupId = group.Id, Name = names[i], SortOrder = i };
            }
        }

        var paymentCategories = accounts.Where(a => a.IsOnBudget && AccountTypeInfo.IsCredit(a.Type)).Select((a, i) => new Category
        {
            Id = ids.Next(start, 300 + i),
            GroupId = SystemIds.CreditCardPaymentsGroup,
            Name = a.Name,
            SortOrder = i,
            IsSystem = true,
            LinkedAccountId = a.Id,
        }).ToList();

        var payees = new Dictionary<string, Payee>(StringComparer.Ordinal);
        Payee PayeeFor(string name)
        {
            if (!payees.TryGetValue(name, out var payee))
            {
                payee = new Payee { Id = ids.Next(start, 400 + payees.Count), Name = name, NormalizedName = PayeeNames.Normalize(name) };
                payees[name] = payee;
            }

            return payee;
        }

        // Ledger rows (not yet with ids): recurring events first, then spending to fill the count.
        var rows = new List<Row>(options.TransactionCount);
        var rta = SystemIds.ReadyToAssignCategory;
        foreach (var account in accounts)
        {
            var spec = AccountSpecs.Single(s => s.Name == account.Name);
            rows.Add(new Row(account.Id, start, PayeeFor("Starting Balance").Id, "Starting Balance", spec.Opening,
                account.IsOnBudget && !AccountTypeInfo.IsLiability(account.Type) ? rta : null, TransactionSource.System));
        }

        var everyday = byName["Everyday Checking"].Id;
        var bills = byName["Bills Checking"].Id;
        var savings = byName["Emergency Savings"].Id;
        var wallet = byName["Wallet"].Id;
        var visa = byName["Visa Rewards"].Id;
        var master = byName["Travel Mastercard"].Id;
        var mortgage = byName["Home Mortgage"].Id;
        var brokerage = byName["Brokerage"].Id;

        void Transfer(Guid from, Guid to, DateOnly date, long amount, Guid? category, string? memo = null)
        {
            var pair = ids.Next(date, 999_000 + rows.Count);
            rows.Add(new Row(from, date, null, string.Empty, -amount, category, TransactionSource.Manual, memo, to, pair));
            rows.Add(new Row(to, date, null, string.Empty, amount, null, TransactionSource.Manual, memo, from, pair));
        }

        void Bill(Guid account, DateOnly date, string payee, string category, long min, long max)
        {
            rows.Add(new Row(account, date, PayeeFor(payee).Id, payee, -Between(random, min, max), categories[category].Id, TransactionSource.Manual));
        }

        var brokerageBalance = 45_000_00L;
        var snapshots = new List<BalanceSnapshot>();
        for (var month = new DateOnly(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            DateOnly Day(int day) => new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)));
            bool In(DateOnly d) => d > start && d <= end;

            if (In(Day(1)))
            {
                Transfer(bills, mortgage, Day(1), 2_100_00, categories["Rent/Mortgage"].Id, "mortgage payment");
            }

            if (In(Day(3)))
            {
                Bill(bills, Day(3), "City Power & Light", "Electric", 60_00, 180_00);
                Bill(bills, Day(3), "Municipal Water", "Water", 30_00, 70_00);
            }

            if (In(Day(5)))
            {
                Bill(visa, Day(5), "Comcast Xfinity", "Internet", 79_99, 79_99);
                Bill(visa, Day(5), "Verizon Wireless", "Phone", 85_00, 95_00);
                Bill(visa, Day(5), "Netflix", "Streaming", 15_49, 15_49);
                Bill(visa, Day(5), "Spotify", "Streaming", 10_99, 10_99);
                Bill(master, Day(5), "New York Times", "Subscriptions", 4_00, 4_00);
            }

            if (In(Day(10)))
            {
                Bill(bills, Day(10), "State Farm", "Insurance", 142_00, 142_00);
                Bill(everyday, Day(10), "Planet Fitness", "Fitness", 24_99, 24_99);
                Bill(bills, Day(10), "Little Sprouts Daycare", "Childcare", 850_00, 850_00);
                Transfer(everyday, wallet, Day(10), 200_00, null, "ATM");
            }

            if (In(Day(15)))
            {
                Transfer(everyday, savings, Day(15), 300_00, null);
                Transfer(everyday, brokerage, Day(15), 500_00, categories["Investing"].Id, "monthly contribution");
            }

            if (In(Day(20)))
            {
                Transfer(everyday, visa, Day(20), Between(random, 1_200_00, 1_900_00), null);
                Transfer(everyday, master, Day(20), Between(random, 200_00, 600_00), null);
            }

            var monthEnd = Day(31);
            if (In(monthEnd))
            {
                brokerageBalance += 500_00 + (brokerageBalance * Between(random, -30, 45) / 1_000);
                snapshots.Add(new BalanceSnapshot { AccountId = brokerage, Date = monthEnd, Balance = brokerageBalance, Source = BalanceSource.Manual });
            }
        }

        // Biweekly paychecks (Fridays) into Everyday Checking, and a transfer to Bills Checking.
        var payday = start.AddDays(((int)DayOfWeek.Friday - (int)start.DayOfWeek + 7) % 7 + 1);
        for (var d = payday; d <= end; d = d.AddDays(14))
        {
            rows.Add(new Row(everyday, d, PayeeFor("Acme Corp Payroll").Id, "Acme Corp Payroll", 2_850_00, rta, TransactionSource.Manual, "paycheck"));
            Transfer(everyday, bills, d, 1_400_00, null);
        }

        // Day-to-day spending fills the rest, spread evenly across the period.
        var spendingCount = Math.Max(0, options.TransactionCount - rows.Count);
        var totalWeight = Spending.Sum(s => s.Weight);
        var spendingAccounts = new (Guid Id, int Weight)[] { (visa, 45), (everyday, 25), (master, 15), (wallet, 10), (bills, 5) };
        var splitSources = new List<(int RowIndex, string[] Categories)>();
        for (var i = 0; i < spendingCount; i++)
        {
            var date = start.AddDays(1 + (int)((long)i * (days - 1) / Math.Max(1, spendingCount)));
            var pick = random.Next(totalWeight);
            var spec = Spending[0];
            foreach (var candidate in Spending)
            {
                if (pick < candidate.Weight)
                {
                    spec = candidate;
                    break;
                }

                pick -= candidate.Weight;
            }

            var accountPick = random.Next(100);
            var account = spendingAccounts[0].Id;
            foreach (var (id, weight) in spendingAccounts)
            {
                if (accountPick < weight)
                {
                    account = id;
                    break;
                }

                accountPick -= weight;
            }

            var payeeName = spec.Payees[random.Next(spec.Payees.Length)];
            var amount = -Between(random, spec.Min * 100L, spec.Max * 100L);
            var memo = random.Next(100) < 15 ? Memos[random.Next(Memos.Length)] : null;
            var isRefund = random.Next(400) == 0;
            rows.Add(new Row(account, date, PayeeFor(payeeName).Id, payeeName, isRefund ? -amount / 2 : amount, categories[spec.Category].Id, TransactionSource.Manual, memo));
            if (!isRefund && random.Next(100) < 2 && spec.Category is "Groceries" or "Household" or "Pharmacy")
            {
                splitSources.Add((rows.Count - 1, [spec.Category, "Household", "Personal Care"]));
            }
        }

        // Order by date (stable), then assign time-ordered ids and statuses.
        var ordered = rows.Select((r, i) => (Row: r, Index: i)).OrderBy(x => x.Row.Date).ThenBy(x => x.Index).ToList();
        var ids2 = new Guid[rows.Count];
        var sequence = 0;
        DateOnly? currentDate = null;
        foreach (var (row, index) in ordered)
        {
            sequence = row.Date == currentDate ? sequence + 1 : 0;
            currentDate = row.Date;
            ids2[index] = ids.Next(row.Date, 1_000 + sequence);
        }

        var db = factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            db.Accounts.AddRange(accounts);
            db.CategoryGroups.AddRange(groups);
            db.Categories.AddRange(categories.Values);
            db.Categories.AddRange(paymentCategories);
            db.Payees.AddRange(payees.Values);
            db.BalanceSnapshots.AddRange(snapshots);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            var connection = db.Database.GetDbConnection();
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var splitCount = await InsertAsync(connection, transaction, rows, ids2, splitSources, categories, random, end, createdAt, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new LedgerFixture(
                    accounts.ToDictionary(a => a.Name, a => a.Id, StringComparer.Ordinal),
                    categories.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.Ordinal),
                    rows.Count,
                    splitCount,
                    rows.Min(r => r.Date),
                    rows.Max(r => r.Date));
            }
        }
    }

    private static async Task<int> InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        List<Row> rows,
        Guid[] ids,
        List<(int RowIndex, string[] Categories)> splitSources,
        Dictionary<string, Category> categories,
        Random random,
        DateOnly end,
        DateTime createdAt,
        CancellationToken ct)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO "Transactions" ("Id", "AccountId", "Date", "PayeeId", "PayeeRaw", "Memo", "Amount", "CategoryId",
                "TransferAccountId", "TransferPairId", "Status", "IsApproved", "Source", "IsDeleted", "CreatedAt", "UpdatedAt")
            VALUES (@id, @account, @date, @payee, @payeeRaw, @memo, @amount, @category,
                @transferAccount, @pair, @status, @approved, @source, 0, @created, @created)
            """;
        var names = new[] { "@id", "@account", "@date", "@payee", "@payeeRaw", "@memo", "@amount", "@category", "@transferAccount", "@pair", "@status", "@approved", "@source", "@created" };
        var p = names.ToDictionary(n => n, n =>
        {
            var parameter = insert.CreateParameter();
            parameter.ParameterName = n;
            insert.Parameters.Add(parameter);
            return parameter;
        });
        await insert.PrepareAsync(ct).ConfigureAwait(false);

        var splitParents = splitSources.ToDictionary(s => s.RowIndex, s => s.Categories);
        var created = createdAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var age = end.DayNumber - row.Date.DayNumber;
            var status = age > 45 ? TransactionStatus.Reconciled : age > 7 ? TransactionStatus.Cleared : TransactionStatus.Uncleared;
            var approved = age > 10 || row.Source == TransactionSource.System || random.Next(100) >= 30;
            p["@id"].Value = Key(ids[i]);
            p["@account"].Value = Key(row.AccountId);
            p["@date"].Value = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            p["@payee"].Value = row.PayeeId is { } payee ? Key(payee) : DBNull.Value;
            p["@payeeRaw"].Value = row.PayeeRaw;
            p["@memo"].Value = (object?)row.Memo ?? DBNull.Value;
            p["@amount"].Value = row.Amount;
            p["@category"].Value = splitParents.ContainsKey(i) || row.CategoryId is null ? DBNull.Value : Key(row.CategoryId.Value);
            p["@transferAccount"].Value = row.TransferAccountId is { } other ? Key(other) : DBNull.Value;
            p["@pair"].Value = row.PairId is { } pair ? Key(pair) : DBNull.Value;
            p["@status"].Value = status.ToString();
            p["@approved"].Value = approved ? 1 : 0;
            p["@source"].Value = row.Source.ToString();
            p["@created"].Value = created;
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var split = connection.CreateCommand();
        split.Transaction = transaction;
        split.CommandText = """
            INSERT INTO "TransactionSplits" ("Id", "TransactionId", "CategoryId", "TransferAccountId", "Memo", "Amount")
            VALUES (@id, @parent, @category, NULL, @memo, @amount)
            """;
        var sp = new[] { "@id", "@parent", "@category", "@memo", "@amount" }.ToDictionary(n => n, n =>
        {
            var parameter = split.CreateParameter();
            parameter.ParameterName = n;
            split.Parameters.Add(parameter);
            return parameter;
        });

        var count = 0;
        foreach (var (rowIndex, splitCategories) in splitSources)
        {
            var parent = rows[rowIndex];
            var lines = 2 + random.Next(2);
            var parts = new Money(parent.Amount, Currency.Default).Allocate(Enumerable.Range(0, lines).Select(_ => (long)random.Next(1, 10)).ToList());
            for (var j = 0; j < lines; j++)
            {
                var splitBytes = ids[rowIndex].ToByteArray(bigEndian: true);
                splitBytes[15] ^= (byte)(j + 1);
                sp["@id"].Value = Key(new Guid(splitBytes, bigEndian: true));
                sp["@parent"].Value = Key(ids[rowIndex]);
                sp["@category"].Value = Key(categories[splitCategories[j % splitCategories.Length]].Id);
                sp["@memo"].Value = j == 0 ? "main items" : DBNull.Value;
                sp["@amount"].Value = parts[j].Amount;
                await split.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    private static long Between(Random random, long min, long max) => min >= max ? min : min + random.NextInt64(max - min + 1);

    private static string Key(Guid id) => RunningBalance.SortKey(id);

    private sealed record Row(
        Guid AccountId,
        DateOnly Date,
        Guid? PayeeId,
        string PayeeRaw,
        long Amount,
        Guid? CategoryId,
        TransactionSource Source,
        string? Memo = null,
        Guid? TransferAccountId = null,
        Guid? PairId = null);

    // Deterministic version-7 GUIDs: a synthetic millisecond timestamp (date + sequence) and seeded random bits.
    private sealed class IdSource(Random random)
    {
        public Guid Next(DateOnly date, int sequence)
        {
            var ms = ((long)date.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber) * 86_400_000L + sequence;
            Span<byte> bytes = stackalloc byte[16];
            Span<byte> timestamp = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(timestamp, ms);
            timestamp[2..].CopyTo(bytes);
            random.NextBytes(bytes[6..]);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes, bigEndian: true);
        }
    }
}
