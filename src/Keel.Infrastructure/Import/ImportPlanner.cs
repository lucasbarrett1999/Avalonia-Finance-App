using System.Globalization;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Import;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Import;

/// <summary>What the pipeline does with one incoming row.</summary>
internal enum ImportRowAction
{
    /// <summary>Nothing: a duplicate, or unchecked by the user.</summary>
    Skip,

    /// <summary>Insert a new transaction.</summary>
    Insert,

    /// <summary>Update the matched row in place (provider id, pending to posted).</summary>
    Update,

    /// <summary>Attach the provider id and fingerprint to the matched manual row.</summary>
    Match,
}

/// <summary>The plan for one incoming row.</summary>
internal sealed class PlannedRow(int index, IncomingTransaction incoming, DedupResult dedup)
{
    public int Index { get; } = index;

    public IncomingTransaction Incoming { get; } = incoming;

    public DedupResult Dedup { get; } = dedup;

    public DedupOutcome Outcome { get; } = DedupOutcomes.From(dedup.Decision, dedup.HasChanges);

    /// <summary>Unchanged provider-id matches have nothing to apply, so the user cannot include them.</summary>
    public bool CanOverride => !(Dedup.Decision == DedupDecision.UpdateByProviderId && !Dedup.HasChanges);

    public bool IncludeByDefault => Outcome != DedupOutcome.DuplicateSkipped;

    public bool Include { get; set; }

    public ImportRowAction Action => !Include ? ImportRowAction.Skip : Outcome switch
    {
        DedupOutcome.Insert or DedupOutcome.DuplicateSkipped => ImportRowAction.Insert,
        DedupOutcome.UpdateInPlace or DedupOutcome.PendingToPosted => ImportRowAction.Update,
        DedupOutcome.MatchedToExisting => ImportRowAction.Match,
        _ => ImportRowAction.Skip,
    };

    /// <summary>Id of the transaction an insert creates (assigned before insert so transfers can pair).</summary>
    public Guid NewId { get; } = EntityIds.New();

    public ImportDraft? Draft { get; set; }

    /// <summary>Resolved existing payee, or null.</summary>
    public Guid? PayeeId { get; set; }

    /// <summary>Name of a payee the import creates for this row (when no payee has that name).</summary>
    public string? NewPayeeName { get; set; }

    public Guid? TransferPartnerId { get; set; }

    public Guid? TransferAccountId { get; set; }

    public bool TransferAmbiguous { get; set; }

    public bool PairByDefault { get; set; }

    public bool Pair { get; set; }

    public bool IsPaired => Action == ImportRowAction.Insert && Pair && TransferPartnerId is not null;
}

/// <summary>The classified batch: every row's action plus what the write step needs.</summary>
internal sealed class ImportPlan
{
    public required Account Account { get; init; }

    public required IReadOnlyList<PlannedRow> Rows { get; init; }

    public required IReadOnlyDictionary<Guid, bool> OnBudgetByAccount { get; init; }

    public required List<ImportWarning> Warnings { get; init; }
}

/// <summary>
/// F-TXN-1 steps 1 to 5 without writing: normalize, dedup against the database (PRD 6.5), payee
/// rename and categorization through <see cref="IImportCategorizationHook"/>, payee default
/// categories, and transfer detection. Shared by the preview and the import, which runs it inside
/// its unit of work so the plan and the write see the same data.
/// </summary>
internal static class ImportPlanner
{
    /// <summary>Rows per SQL <c>IN</c> list.</summary>
    private const int ChunkSize = 500;

    public static async Task<ImportPlan> PlanAsync(
        KeelDbContext db,
        TransactionSource source,
        ImportBatch batch,
        IReadOnlyList<IImportCategorizationHook> hooks,
        bool isPreview,
        CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == batch.AccountId, ct).ConfigureAwait(false)
            ?? throw new LedgerValidationException(LedgerError.AccountNotFound);
        if (account.IsClosed)
        {
            throw new LedgerValidationException(LedgerError.AccountClosed);
        }

        var accounts = await db.Accounts.AsNoTracking()
            .Select(a => new { a.Id, a.IsOnBudget, a.IsClosed, a.SyncConnectionId })
            .ToListAsync(ct).ConfigureAwait(false);
        var onBudget = accounts.ToDictionary(a => a.Id, a => a.IsOnBudget);
        var warnings = new List<ImportWarning>(batch.Warnings);
        var incoming = batch.Transactions;
        if (incoming.Count == 0)
        {
            return new ImportPlan { Account = account, Rows = [], OnBudgetByAccount = onBudget, Warnings = warnings };
        }

        var from = incoming.Min(r => r.Date).AddDays(-TransferDetector.DefaultMaxDays);
        var to = incoming.Max(r => r.Date).AddDays(TransferDetector.DefaultMaxDays);

        // Step 2 inputs: rows of the target account in the batch's window, plus provider-id lookups
        // (any date) within the account or, for a sync batch, the connection's accounts.
        var connectionOf = accounts.ToDictionary(a => a.Id, a => a.SyncConnectionId);
        var scope = batch.SyncConnectionId is { } connection
            ? accounts.Where(a => a.SyncConnectionId == connection).Select(a => a.Id).Append(account.Id).Distinct().ToList()
            : [account.Id];
        var existing = new Dictionary<Guid, CandidateRow>();
        foreach (var row in await Candidates(db.Transactions.Where(t => t.AccountId == account.Id && t.Date >= from && t.Date <= to), db)
            .ToListAsync(ct).ConfigureAwait(false))
        {
            existing[row.Id] = row;
        }

        var keys = incoming.SelectMany(r => new[] { r.ProviderTransactionId, r.ProviderPendingId })
            .Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in keys.Chunk(ChunkSize))
        {
            var query = db.Transactions.Where(t => scope.Contains(t.AccountId)
                && ((t.ProviderTransactionId != null && chunk.Contains(t.ProviderTransactionId))
                    || (t.ProviderPendingId != null && chunk.Contains(t.ProviderPendingId))));
            foreach (var row in await Candidates(query, db).ToListAsync(ct).ConfigureAwait(false))
            {
                existing[row.Id] = row;
            }
        }

        var candidates = existing.Values.Select(c => new DedupCandidate(
            c.Id,
            c.AccountId,
            c.Date,
            c.Amount,
            PayeeNormalizer.Normalize(c.Source is TransactionSource.Manual or TransactionSource.Scheduled ? c.PayeeName ?? c.PayeeRaw : c.PayeeRaw),
            c.Source,
            c.ProviderTransactionId,
            c.ProviderPendingId,
            c.ImportFingerprint,
            connectionOf.GetValueOrDefault(c.AccountId),
            c.HasImportMatch)).ToList();

        // Steps 1 and 2: normalize and dedup.
        var results = DuplicateMatcher.Classify(account.Id, incoming, candidates, batch.SyncConnectionId);
        var rows = new List<PlannedRow>(incoming.Count);
        foreach (var result in results)
        {
            var row = new PlannedRow(result.Index, incoming[result.Index], result);
            row.Include = row.CanOverride && batch.Overrides is { } overrides && overrides.TryGetValue(row.Index, out var choice)
                ? choice.Include
                : row.IncludeByDefault;
            rows.Add(row);
        }

        await CategorizeAsync(db, source, account, rows, hooks, isPreview, ct).ConfigureAwait(false);
        await DetectTransfersAsync(db, account, rows, accounts.Where(a => !a.IsClosed && a.Id != account.Id).Select(a => a.Id).ToHashSet(), from, to, batch, ct).ConfigureAwait(false);
        return new ImportPlan { Account = account, Rows = rows, OnBudgetByAccount = onBudget, Warnings = warnings };
    }

    /// <summary>The display name of a new payee made from a normalized descriptor: "TRADER JOES" → "Trader Joes".</summary>
    public static string DisplayName(string normalizedPayee) =>
        string.IsNullOrEmpty(normalizedPayee)
            ? string.Empty
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalizedPayee.ToLowerInvariant());

    // Steps 3 and 4: payee (existing payee with the same normalized name, else a title-cased one),
    // rename hooks, payee default category, categorization hooks.
    private static async Task CategorizeAsync(
        KeelDbContext db,
        TransactionSource source,
        Account account,
        List<PlannedRow> rows,
        IReadOnlyList<IImportCategorizationHook> hooks,
        bool isPreview,
        CancellationToken ct)
    {
        var inserts = rows.Where(r => r.Action == ImportRowAction.Insert).ToList();
        if (inserts.Count == 0)
        {
            return;
        }

        var payees = await db.Payees.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.NormalizedName, p.DefaultCategoryId })
            .ToListAsync(ct).ConfigureAwait(false);
        var byImportName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in payees.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            byImportName.TryAdd(PayeeNormalizer.Normalize(p.Name), p.Name);
        }

        var byKey = payees.ToDictionary(p => p.NormalizedName, p => (p.Id, p.DefaultCategoryId), StringComparer.Ordinal);
        var drafts = inserts.Select(r =>
        {
            var normalized = r.Dedup.NormalizedPayee;
            var name = byImportName.TryGetValue(normalized, out var known) ? known : DisplayName(normalized);
            return r.Draft = new ImportDraft(r.Index, r.Incoming, normalized, name);
        }).ToList();

        var context = new ImportHookContext(source, account.Id, account.IsOnBudget, isPreview);
        foreach (var hook in hooks)
        {
            await hook.RenamePayeesAsync(context, drafts, ct).ConfigureAwait(false);
        }

        foreach (var row in inserts)
        {
            var draft = row.Draft!;
            var name = PayeeNames.Clean(draft.PayeeName);
            draft.PayeeName = name;
            if (name.Length == 0)
            {
                continue;
            }

            if (byKey.TryGetValue(PayeeNames.Normalize(name), out var payee))
            {
                row.PayeeId = payee.Id;
                draft.CategoryId ??= payee.DefaultCategoryId;
            }
            else
            {
                row.NewPayeeName = name;
            }
        }

        foreach (var hook in hooks)
        {
            await hook.CategorizeAsync(context, drafts, ct).ConfigureAwait(false);
        }

        // Tracking accounts carry no categories; unknown categories are dropped rather than failing the import.
        var validCategories = await db.Categories.AsNoTracking().Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var valid = validCategories.ToHashSet();
        foreach (var draft in drafts)
        {
            if (!account.IsOnBudget || (draft.CategoryId is { } id && !valid.Contains(id)))
            {
                draft.CategoryId = null;
            }
        }
    }

    // Step 5: opposite amounts in the user's other accounts within ±3 days (TransferDetector). The
    // other side is an existing imported row that is not yet a transfer, split or reconciled.
    private static async Task DetectTransfersAsync(
        KeelDbContext db,
        Account account,
        List<PlannedRow> rows,
        HashSet<Guid> otherOpenAccounts,
        DateOnly from,
        DateOnly to,
        ImportBatch batch,
        CancellationToken ct)
    {
        var inserts = rows.Where(r => r.Action == ImportRowAction.Insert && r.Incoming.Amount != 0).ToList();
        if (inserts.Count == 0 || otherOpenAccounts.Count == 0)
        {
            return;
        }

        var wanted = inserts.Select(r => r.Incoming.Amount == long.MinValue ? 0 : -r.Incoming.Amount).ToHashSet();
        var others = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId != account.Id && t.Date >= from && t.Date <= to && t.TransferPairId == null
                && (t.Source == TransactionSource.File || t.Source == TransactionSource.Provider)
                && t.Status != TransactionStatus.Reconciled && !t.Splits.Any())
            .Select(t => new { t.Id, t.AccountId, t.Date, t.Amount })
            .ToListAsync(ct).ConfigureAwait(false);

        var candidates = inserts
            .Select(r => new TransferCandidate(r.NewId, account.Id, r.Incoming.Date, r.Incoming.Amount, IsIncoming: true))
            .Concat(others
                .Where(o => otherOpenAccounts.Contains(o.AccountId) && wanted.Contains(o.Amount))
                .Select(o => new TransferCandidate(o.Id, o.AccountId, o.Date, o.Amount, IsIncoming: false)))
            .ToList();
        var byNewId = inserts.ToDictionary(r => r.NewId);
        var accountOf = candidates.ToDictionary(c => c.Id, c => c.AccountId);
        foreach (var match in TransferDetector.Detect(candidates))
        {
            var (mine, partner) = byNewId.TryGetValue(match.OutflowId, out var row) ? (row, match.InflowId)
                : byNewId.TryGetValue(match.InflowId, out row) ? (row, match.OutflowId)
                : (null, Guid.Empty);
            if (mine is null || byNewId.ContainsKey(partner))
            {
                continue;
            }

            mine.TransferPartnerId = partner;
            mine.TransferAccountId = accountOf[partner];
            mine.TransferAmbiguous = match.IsAmbiguous;
            mine.PairByDefault = !match.IsAmbiguous;
            mine.Pair = batch.Overrides is { } overrides && overrides.TryGetValue(mine.Index, out var choice)
                ? choice.PairAsTransfer
                : mine.PairByDefault;
        }
    }

    private static IQueryable<CandidateRow> Candidates(IQueryable<Transaction> query, KeelDbContext db) =>
        query.AsNoTracking().Select(t => new CandidateRow(
            t.Id,
            t.AccountId,
            t.Date,
            t.Amount,
            t.PayeeRaw,
            db.Payees.Where(p => p.Id == t.PayeeId).Select(p => p.Name).FirstOrDefault(),
            t.Source,
            t.ProviderTransactionId,
            t.ProviderPendingId,
            t.ImportFingerprint,
            t.HasImportMatch));

    private sealed record CandidateRow(
        Guid Id,
        Guid AccountId,
        DateOnly Date,
        long Amount,
        string PayeeRaw,
        string? PayeeName,
        TransactionSource Source,
        string? ProviderTransactionId,
        string? ProviderPendingId,
        string? ImportFingerprint,
        bool HasImportMatch);
}
