using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Import.Migration;

/// <summary>
/// Imports YNAB and Monarch exports (PRD 9.10, ADR 0099). One migration is one <see cref="LedgerWriter"/>
/// unit of work: new accounts, category groups and categories are added first, then each source account's
/// rows run through <see cref="ImportService.ImportCoreAsync"/> (normalize, dedup, payees, rules and learner
/// hooks, transfers, bulk insert) with the file's category, cleared state, approval and tags; so the whole
/// migration is audited, undone as one action and re-importing the same file adds nothing. The preview is
/// the same work rolled back (<see cref="LedgerWriter.DryRunAsync{T}"/>).
/// </summary>
public sealed partial class MigrationImportService(
    IDbContextFactory<KeelDbContext> factory,
    LedgerWriter writer,
    IEnumerable<IImportCategorizationHook> hooks,
    ILogger<MigrationImportService> logger) : IMigrationImportService
{
    private readonly IReadOnlyList<IImportCategorizationHook> _hooks = hooks.ToList();

    /// <inheritdoc />
    public Task<MigrationPlan> PlanAsync(ParseResult parsed, CancellationToken ct)
    {
        RequireTransactions(parsed);
        return Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var accounts = await db.Accounts.AsNoTracking().Where(a => !a.IsClosed).Select(a => new { a.Id, a.Name }).ToListAsync(ct).ConfigureAwait(false);
                    var catalog = await CategoryCatalog.LoadAsync(db, track: false, ct).ConfigureAwait(false);
                    var tags = (await db.Tags.AsNoTracking().Select(t => t.Name).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var plans = new List<MigrationAccountPlan>();
                    foreach (var (source, rows) in BySourceAccount(parsed))
                    {
                        var match = accounts.FirstOrDefault(a => string.Equals(a.Name, source.Trim(), StringComparison.OrdinalIgnoreCase));
                        plans.Add(new MigrationAccountPlan(
                            source,
                            rows.Count,
                            rows.Min(r => r.Date),
                            rows.Max(r => r.Date),
                            rows.Sum(r => r.Amount),
                            match?.Id,
                            MigrationRows.SuggestType(source)));
                    }

                    var newCategories = parsed.Transactions
                        .Select(r => MigrationRows.CategoryOf(parsed.Format, r))
                        .Where(c => c.Kind == MigrationCategoryKind.Named && catalog.Find(c.Group, c.Name) is null)
                        .Select(c => new MigrationCategoryName(PayeeNames.Clean(c.Group), PayeeNames.Clean(c.Name)))
                        .DistinctBy(c => (c.Group.ToUpperInvariant(), c.Name.ToUpperInvariant()))
                        .ToList();
                    var newTags = parsed.Transactions
                        .SelectMany(r => MigrationRows.TagsOf(parsed.Format, r))
                        .Select(PayeeNames.Clean)
                        .Where(t => t.Length > 0 && !tags.Contains(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return new MigrationPlan(parsed.Format, plans, newCategories, newTags) { Warnings = parsed.Warnings };
                }
            },
            ct);
    }

    /// <inheritdoc />
    public Task<MigrationSummary> PreviewAsync(MigrationRequest request, CancellationToken ct)
    {
        Validate(request);
        return writer.DryRunAsync(session => MigrateAsync(session, request, ct), ct);
    }

    /// <inheritdoc />
    public async Task<MigrationSummary> ImportAsync(MigrationRequest request, CancellationToken ct)
    {
        Validate(request);
        var summary = await writer.RunAsync(LedgerAction.ImportTransactions, session => MigrateAsync(session, request, ct), ct).ConfigureAwait(false);
        LogMigrated(logger, request.Parsed.Format, summary.Accounts.Count, summary.AccountsCreated, summary.Added, summary.Duplicates, summary.CategoriesCreated);
        return summary;
    }

    /// <inheritdoc />
    public Task<BudgetImportSummary> PreviewBudgetAsync(ParseResult parsed, CancellationToken ct)
    {
        RequireBudget(parsed);
        return writer.DryRunAsync(session => ImportBudgetCoreAsync(session, parsed, ct), ct);
    }

    /// <inheritdoc />
    public async Task<BudgetImportSummary> ImportBudgetAsync(ParseResult parsed, CancellationToken ct)
    {
        RequireBudget(parsed);
        var summary = await writer.RunAsync(LedgerAction.AssignBudget, session => ImportBudgetCoreAsync(session, parsed, ct), ct).ConfigureAwait(false);
        LogBudgetImported(logger, summary.Months, summary.Assignments, summary.CategoriesCreated);
        return summary;
    }

    private async Task<MigrationSummary> MigrateAsync(LedgerSession session, MigrationRequest request, CancellationToken ct)
    {
        var db = session.Db;
        var parsed = request.Parsed;
        var currency = Currency.Normalize(request.Currency);
        var catalog = await CategoryCatalog.LoadAsync(db, track: true, ct).ConfigureAwait(false);
        var choices = request.Accounts.Where(c => !c.Skip).ToDictionary(c => c.SourceAccount, StringComparer.Ordinal);
        var bySource = BySourceAccount(parsed).Where(s => choices.ContainsKey(s.Source)).ToList();

        // Accounts first, so the pipeline finds them; then every category the chosen rows name.
        var targets = new List<(string Source, Account Account, bool Created, List<ParsedTransaction> Rows)>();
        foreach (var (source, rows) in bySource)
        {
            var choice = choices[source];
            if (choice.ExistingAccountId is { } id)
            {
                var existing = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.AccountNotFound);
                targets.Add((source, existing, false, rows));
            }
            else
            {
                targets.Add((source, await CreateAccountAsync(db, choice, currency, rows.Min(r => r.Date), ct).ConfigureAwait(false), true, rows));
            }
        }

        var categoryOf = new Dictionary<int, Guid?>();
        foreach (var row in targets.SelectMany(t => t.Rows))
        {
            var reference = MigrationRows.CategoryOf(parsed.Format, row);
            categoryOf[row.SourceIndex] = reference.Kind switch
            {
                MigrationCategoryKind.ReadyToAssign => SystemIds.ReadyToAssignCategory,
                MigrationCategoryKind.Named => catalog.FindOrAdd(reference.Group, reference.Name).Id,
                _ => null,
            };
        }

        await session.SaveAsync(ct).ConfigureAwait(false);
        var tagsBefore = await db.Tags.CountAsync(ct).ConfigureAwait(false);

        var results = new List<MigrationAccountResult>();
        foreach (var (source, account, created, rows) in targets)
        {
            var incoming = rows.Select(r =>
            {
                var category = categoryOf[r.SourceIndex];
                var reference = MigrationRows.CategoryOf(parsed.Format, r);
                return new IncomingTransaction(r.Date, r.Amount, r.PayeeRaw, r.Memo, CategoryId: category)
                {
                    CategoryHint = r.Category,
                    Status = MigrationRows.StatusOf(parsed.Format, r),
                    IsApproved = category is not null || reference.IsTransfer,
                    Tags = MigrationRows.TagsOf(parsed.Format, r),
                };
            }).ToList();
            var summary = await ImportService.ImportCoreAsync(session, TransactionSource.File, new ImportBatch(account.Id, incoming), _hooks, ct).ConfigureAwait(false);
            results.Add(new MigrationAccountResult(source, account.Id, account.Name, created, summary));
        }

        var tagsAfter = await db.Tags.CountAsync(ct).ConfigureAwait(false);
        return new MigrationSummary(results, catalog.GroupsCreated, catalog.CategoriesCreated, tagsAfter - tagsBefore);
    }

    // As IAccountService.CreateAccount, inside this unit of work: on budget, the default profile, next in its
    // sidebar group, and a Credit Card Payment category for credit accounts (PRD 6.4.5). No starting-balance
    // row: the export's own rows (YNAB's "Starting Balance") make up the balance.
    private static async Task<Account> CreateAccountAsync(KeelDbContext db, MigrationAccountChoice choice, string currency, DateOnly opened, CancellationToken ct)
    {
        var name = PayeeNames.Clean(choice.NewAccountName);
        if (name.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.AccountNameRequired);
        }

        if (!AccountTypeInfo.IsOnBudgetAllowed(choice.NewAccountType, true) || !AccountTypeInfo.IsOnBudgetByDefault(choice.NewAccountType))
        {
            throw new LedgerValidationException(LedgerError.OnBudgetNotAllowed);
        }

        var account = Account.Create(name, choice.NewAccountType, opened, currency);
        account.IsOnBudget = true;
        account.OwnerProfileId = SystemIds.DefaultProfile;
        var inGroup = (await db.Accounts.AsNoTracking().Select(a => new { a.Type, a.IsOnBudget, a.SortOrder }).ToListAsync(ct).ConfigureAwait(false))
            .Concat(db.Accounts.Local.Select(a => new { a.Type, a.IsOnBudget, a.SortOrder }))
            .Where(a => AccountTypeInfo.GroupOf(a.Type, a.IsOnBudget) == account.Group)
            .Select(a => a.SortOrder)
            .ToList();
        account.SortOrder = inGroup.Count == 0 ? 0 : inGroup.Max() + 1;
        db.Accounts.Add(account);

        if (AccountTypeInfo.IsCredit(account.Type))
        {
            var sort = await db.Categories.Where(c => c.GroupId == SystemIds.CreditCardPaymentsGroup)
                .Select(c => (int?)c.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            sort = Math.Max(sort, db.Categories.Local.Where(c => c.GroupId == SystemIds.CreditCardPaymentsGroup).Select(c => c.SortOrder).DefaultIfEmpty(-1).Max());
            db.Categories.Add(new Category
            {
                GroupId = SystemIds.CreditCardPaymentsGroup,
                Name = account.Name,
                SortOrder = sort + 1,
                IsSystem = true,
                LinkedAccountId = account.Id,
            });
        }

        return account;
    }

    // A YNAB budget export: the assigned amount of each category and month (absent row = 0). Inflow rows are
    // left out (Ready to Assign is computed, PRD 6.4); credit card payment rows go to the card with that name.
    private static async Task<BudgetImportSummary> ImportBudgetCoreAsync(LedgerSession session, ParseResult parsed, CancellationToken ct)
    {
        var db = session.Db;
        var catalog = await CategoryCatalog.LoadAsync(db, track: true, ct).ConfigureAwait(false);
        var rows = parsed.BudgetRows;
        var months = rows.Select(r => r.Month).Distinct().Order().ToList();
        var skipped = 0;
        var wanted = new Dictionary<(Guid Category, DateOnly Month), long>();
        foreach (var row in rows)
        {
            Guid? category;
            if (string.Equals(row.Group.Trim(), MigrationRows.YnabInflowGroup, StringComparison.OrdinalIgnoreCase))
            {
                category = null;
            }
            else if (string.Equals(row.Group.Trim(), MigrationRows.YnabCreditCardGroup, StringComparison.OrdinalIgnoreCase))
            {
                category = catalog.FindCreditCardPayment(row.Category)?.Id;
            }
            else
            {
                category = catalog.FindOrAdd(row.Group, row.Category).Id;
            }

            if (category is { } id)
            {
                wanted[(id, row.Month)] = row.Assigned; // a repeated row: the last one wins
            }
            else
            {
                skipped++;
            }
        }

        await session.SaveAsync(ct).ConfigureAwait(false);
        var first = months.Count == 0 ? default : months[0];
        var last = months.Count == 0 ? default : months[^1];
        var existing = await db.BudgetAssignments.Where(b => b.Month >= first && b.Month <= last).ToListAsync(ct).ConfigureAwait(false);
        var byKey = existing.ToDictionary(b => (b.CategoryId, b.Month));
        int written = 0, unchanged = 0;
        foreach (var ((categoryId, month), amount) in wanted)
        {
            if (byKey.TryGetValue((categoryId, month), out var assignment))
            {
                if (assignment.Assigned == amount)
                {
                    unchanged++;
                    continue;
                }

                if (amount == 0)
                {
                    db.BudgetAssignments.Remove(assignment);
                }
                else
                {
                    assignment.Assigned = amount;
                }

                written++;
            }
            else if (amount == 0)
            {
                unchanged++;
            }
            else
            {
                db.BudgetAssignments.Add(new BudgetAssignment { CategoryId = categoryId, Month = month, Assigned = amount });
                written++;
            }
        }

        return new BudgetImportSummary(months.Count, written, unchanged, catalog.GroupsCreated, catalog.CategoriesCreated, skipped)
        {
            FirstMonth = months.Count == 0 ? null : first,
            LastMonth = months.Count == 0 ? null : last,
        };
    }

    // The file's accounts in file order, each with its rows.
    private static List<(string Source, List<ParsedTransaction> Rows)> BySourceAccount(ParseResult parsed)
    {
        var order = parsed.Accounts.Select(a => a.AccountId ?? string.Empty).ToList();
        var groups = parsed.Transactions.GroupBy(r => r.SourceAccountId ?? string.Empty, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return order.Concat(groups.Keys.Except(order, StringComparer.Ordinal))
            .Where(groups.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .Select(name => (name, groups[name]))
            .ToList();
    }

    private static void Validate(MigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTransactions(request.Parsed);
        if (!Currency.IsValidCode(request.Currency?.Trim().ToUpperInvariant()))
        {
            throw new LedgerValidationException(LedgerError.InvalidCurrency);
        }
    }

    private static void RequireTransactions(ParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Format is not (ImportFileFormat.Ynab or ImportFileFormat.Monarch))
        {
            throw new ArgumentException("Only YNAB and Monarch transaction exports are migrated.", nameof(parsed));
        }
    }

    private static void RequireBudget(ParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Format != ImportFileFormat.YnabBudget)
        {
            throw new ArgumentException("Only YNAB budget exports are imported as assignments.", nameof(parsed));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migration from {Format}: {Accounts} accounts ({Created} created), {Added} added, {Duplicates} duplicates, {Categories} categories created")]
    private static partial void LogMigrated(ILogger logger, ImportFileFormat format, int accounts, int created, int added, int duplicates, int categories);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget import: {Months} months, {Assignments} assignments written, {Categories} categories created")]
    private static partial void LogBudgetImported(ILogger logger, int months, int assignments, int categories);
}
