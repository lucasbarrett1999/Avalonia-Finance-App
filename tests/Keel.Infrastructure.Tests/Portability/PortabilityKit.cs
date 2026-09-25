using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Target = Keel.Domain.Entities.Target;

namespace Keel.Infrastructure.Tests.Portability;

/// <summary>Helpers for the export and bundle tests: the rest of PRD 6.2 on top of the ledger fixture, and table hashes.</summary>
internal static class PortabilityKit
{
    /// <summary>A stored row count and a SHA-256 over every stored value, in key order.</summary>
    internal sealed record TableHash(long Rows, string Hash);

    /// <summary>
    /// Adds what the ledger fixture lacks: rules, targets, schedules, recurring items with an alert, tags, a
    /// reconciliation, a sync connection, assignments, notes, settings (including the derived keys a bundle
    /// leaves out), an attachment with its file, a soft-deleted transaction and a renamed default profile.
    /// </summary>
    public static async Task AddEverythingElseAsync(IDbContextFactory<KeelDbContext> factory, LedgerFixture fixture, string attachmentsFolder)
    {
        await using var db = await factory.CreateDbContextAsync();
        var accounts = fixture.Accounts;
        var categories = fixture.Categories;
        var checking = accounts["Everyday Checking"];
        var transactions = await db.Transactions.OrderBy(t => t.Date).ThenBy(t => t.Id).Take(40).ToListAsync();
        var payees = await db.Payees.OrderBy(p => p.NormalizedName).Take(3).ToListAsync();

        (await db.Profiles.SingleAsync(p => p.Id == SystemIds.DefaultProfile)).Name = "Alex";
        (await db.CategoryGroups.SingleAsync(g => g.Id == SystemIds.CreditCardPaymentsGroup)).IsHidden = true;
        var groceries = await db.Categories.SingleAsync(c => c.Id == categories["Groceries"]);
        groceries.Notes = "Weekly shop, \"bulk\" at Costco";
        groceries.FlexKind = FlexKind.Flex;

        var tags = new[] { "Reimbursable", "Vacation 2026", "Flagged" }.Select(n => new Tag { Name = n }).ToList();
        db.Tags.AddRange(tags);
        for (var i = 0; i < 12; i++)
        {
            db.TransactionTags.Add(new TransactionTag { TransactionId = transactions[i].Id, TagId = tags[i % tags.Count].Id });
        }

        transactions[20].IsDeleted = true;
        transactions[21].ProviderTransactionId = "FITID-0001";
        transactions[21].ImportFingerprint = new string('a', 64);
        transactions[21].HasImportMatch = true;

        db.Rules.Add(new Rule { Name = "Coffee", SortOrder = 0, ConditionsJson = """{"v":1,"all":[{"type":"payeeContains","value":"coffee"}]}""", ActionsJson = """{"v":1,"actions":[{"type":"setCategory","categoryId":"x"}]}""" });
        db.Rules.Add(new Rule { Name = "Disabled rule", SortOrder = 1, IsEnabled = false, ContinueAfterMatch = true });

        db.Targets.Add(new Target { CategoryId = categories["Groceries"], Type = TargetType.MonthlySpending, Amount = 600_00 });
        db.Targets.Add(new Target { CategoryId = categories["Vacation Fund"], Type = TargetType.SavingsBalanceByDate, Amount = 3_000_00, TargetDate = new DateOnly(2027, 6, 1) });
        db.Targets.Add(new Target { CategoryId = categories["Insurance"], Type = TargetType.MonthlySetAside, Amount = 1_200_00, Cadence = RecurrenceCadence.Yearly, LinkedAccountId = checking });

        var schedule = new ScheduledTransaction
        {
            AccountId = checking,
            Amount = -15_99,
            PayeeId = payees[0].Id,
            CategoryId = categories["Streaming"],
            Memo = "family plan",
            RecurrenceRule = "DTSTART:20260905\nRRULE:FREQ=MONTHLY;BYMONTHDAY=5",
            NextDate = new DateOnly(2026, 9, 5),
            AutoEnter = true,
        };
        var transfer = new ScheduledTransaction
        {
            AccountId = checking,
            Amount = -500_00,
            PayeeId = payees[1].Id,
            TransferAccountId = accounts["Emergency Savings"],
            RecurrenceRule = "DTSTART:20260915\nRRULE:FREQ=WEEKLY;INTERVAL=2",
            NextDate = new DateOnly(2026, 9, 15),
            EndDate = new DateOnly(2027, 9, 15),
        };
        db.ScheduledTransactions.AddRange(schedule, transfer);
        transactions[22].ScheduledFromId = schedule.Id;

        var streaming = new RecurringItem
        {
            PayeeId = payees[0].Id,
            AccountId = checking,
            Cadence = RecurrenceCadence.Monthly,
            ExpectedAmount = -15_99,
            AmountTolerance = 2_00,
            NextExpectedDate = new DateOnly(2026, 9, 5),
            LastSeenDate = new DateOnly(2026, 8, 5),
            Confidence = 0.9173412345678901,
            Status = RecurringStatus.Active,
            IsSubscription = true,
            CategoryId = categories["Streaming"],
            ScheduledTransactionId = schedule.Id,
        };
        var gym = new RecurringItem
        {
            PayeeId = payees[2].Id,
            Cadence = RecurrenceCadence.Semimonthly,
            ExpectedAmount = -40_00,
            AmountTolerance = 4_00,
            IsVariableAmount = true,
            NextExpectedDate = new DateOnly(2026, 9, 1),
            LastSeenDate = new DateOnly(2026, 8, 16),
            Confidence = 1d / 3,
            Status = RecurringStatus.Detected,
        };
        db.RecurringItems.AddRange(streaming, gym);
        db.Alerts.Add(new Alert
        {
            Kind = AlertKind.PriceIncrease,
            RecurringItemId = streaming.Id,
            TransactionId = transactions[23].Id,
            CreatedAt = new DateTime(2026, 9, 2, 8, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567),
            ReadAt = new DateTime(2026, 9, 3, 1, 2, 3, DateTimeKind.Utc),
            PayloadJson = """{"key":"price:x","old":1499,"new":1599}""",
        });

        var connection = new SyncConnection
        {
            Provider = SyncProvider.SimpleFin,
            InstitutionName = "Credit Union",
            ExternalItemId = "item-1",
            Cursor = "cursor-9",
            Status = SyncStatus.NeedsReauth,
            LastSyncAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            LastError = "ITEM_LOGIN_REQUIRED",
            SecretRef = "connection/item-1",
        };
        db.SyncConnections.Add(connection);
        var savings = await db.Accounts.SingleAsync(a => a.Id == accounts["Emergency Savings"]);
        savings.SyncConnectionId = connection.Id;
        savings.ProviderAccountId = "acct-77";
        savings.ReportedBalance = 12_345_67;
        savings.ReportedBalanceAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        savings.Notes = "High-yield; ünïcødé ✓";

        db.Reconciliations.Add(new Reconciliation { AccountId = checking, StatementDate = new DateOnly(2026, 7, 31), StatementBalance = 4_321_00, CompletedAt = new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc) });
        foreach (var (name, month, amount) in new[] { ("Groceries", 7, 550_00L), ("Groceries", 8, 600_00L), ("Rent/Mortgage", 8, 2_100_00L), ("Vacation Fund", 8, 250_00L) })
        {
            db.BudgetAssignments.Add(new BudgetAssignment { CategoryId = categories[name], Month = new DateOnly(2026, month, 1), Assigned = amount });
        }

        db.Settings.Add(new Setting { Key = "budget.monthNote.2026-08", ValueJson = "\"Back to school\"" });
        db.Settings.Add(new Setting { Key = "recurring.subscriptionTagIds", ValueJson = $"[\"{tags[0].Id}\"]" });
        db.Settings.Add(new Setting { Key = LearnerService.SettingKey, ValueJson = "{\"cache\":true}" });
        db.Settings.Add(new Setting { Key = "recurring.importAuditWatermark", ValueJson = "123456" });

        var receipt = Encoding.UTF8.GetBytes("receipt image bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(receipt));
        db.Attachments.Add(new Attachment { TransactionId = transactions[24].Id, FileName = "receipt.png", Sha256 = sha, MimeType = "image/png" });
        await db.SaveChangesAsync();

        Directory.CreateDirectory(Path.Combine(attachmentsFolder, sha[..2]));
        await File.WriteAllBytesAsync(Path.Combine(attachmentsFolder, sha[..2], sha), receipt);
    }

    /// <summary>
    /// Every EF table of a budget file except the audit log, hashed from the stored values themselves (type and
    /// text of each column, in key order); the derived setting keys a bundle leaves out are not counted.
    /// </summary>
    public static async Task<Dictionary<string, TableHash>> HashTablesAsync(string path)
    {
        await using var db = KeelDbContextFactory.CreateForFile(path);
        var result = new Dictionary<string, TableHash>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        foreach (var type in db.Model.GetEntityTypes().Where(t => t.ClrType != typeof(AuditEvent)))
        {
            var table = type.GetTableName()!;
            var order = string.Join(", ", type.FindPrimaryKey()!.Properties.Select(p => $"\"{p.GetColumnName()}\""));
            var where = type.ClrType == typeof(Setting) ? $"WHERE \"Key\" NOT IN ('{LearnerService.SettingKey}', 'recurring.importAuditWatermark')" : string.Empty;
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" {where} ORDER BY {order}";
            await using var reader = await command.ExecuteReaderAsync();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long rows = 0;
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            hash.AppendData(Encoding.UTF8.GetBytes(string.Join('|', columns.Order(StringComparer.Ordinal))));
            var byName = columns.Select((c, i) => (c, i)).OrderBy(x => x.c, StringComparer.Ordinal).Select(x => x.i).ToList();
            while (await reader.ReadAsync())
            {
                rows++;
                foreach (var i in byName)
                {
                    var value = reader.GetValue(i);
                    var text = value switch
                    {
                        DBNull => "NULL",
                        long l => "i:" + l.ToString(CultureInfo.InvariantCulture),
                        double d => "r:" + d.ToString("R", CultureInfo.InvariantCulture),
                        string s => "t:" + s,
                        byte[] b => "b:" + Convert.ToBase64String(b),
                        _ => "?:" + Convert.ToString(value, CultureInfo.InvariantCulture),
                    };
                    hash.AppendData(Encoding.UTF8.GetBytes(text + "\u001f"));
                }

                hash.AppendData("\u001e"u8);
            }

            result[type.ClrType.Name] = new TableHash(rows, Convert.ToHexString(hash.GetHashAndReset()));
        }

        return result;
    }
}
