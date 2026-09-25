using Keel.Application.Import;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Import;

/// <summary>
/// The unified import pipeline (F-TXN-1) for every source: normalize payees, dedup against the
/// database (PRD 6.5), payee rename and categorization hooks (M4 plugs in through
/// <see cref="IImportCategorizationHook"/>), payee default categories, transfer detection, and
/// the insert as unapproved. An import is one <see cref="LedgerWriter"/> unit of work: one database
/// transaction, audited, undoable as one action, one <c>LedgerChanged</c>.
/// </summary>
public sealed partial class ImportService(
    IDbContextFactory<KeelDbContext> factory,
    LedgerWriter writer,
    IEnumerable<IImportCategorizationHook> hooks,
    ILogger<ImportService> logger) : IImportService
{
    private readonly IReadOnlyList<IImportCategorizationHook> _hooks = hooks.ToList();

    /// <inheritdoc />
    public Task<ImportPreview> PreviewAsync(TransactionSource source, ImportBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var plan = await ImportPlanner.PlanAsync(db, source, batch, _hooks, isPreview: true, ct).ConfigureAwait(false);
                    var rows = plan.Rows.Select(r => new ImportPreviewRow(r.Incoming, r.Outcome, r.Dedup.ExistingId)
                    {
                        Index = r.Index,
                        NormalizedPayee = r.Dedup.NormalizedPayee,
                        PayeeName = r.Draft?.PayeeName ?? string.Empty,
                        CategoryId = r.Draft?.CategoryId,
                        HasChanges = r.Dedup.HasChanges,
                        IncludeByDefault = r.IncludeByDefault,
                        CanOverride = r.CanOverride,
                        TransferAccountId = r.TransferAccountId,
                        TransferPartnerId = r.TransferPartnerId,
                        IsTransferAmbiguous = r.TransferAmbiguous,
                        PairTransferByDefault = r.PairByDefault,
                    }).ToList();
                    return new ImportPreview(rows)
                    {
                        AccountId = plan.Account.Id,
                        Warnings = plan.Warnings,
                        ReportedBalance = batch.ReportedBalance,
                    };
                }
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<ImportSummary> ImportTransactionsAsync(TransactionSource source, ImportBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var summary = await writer.RunAsync(LedgerAction.ImportTransactions, session => ImportCoreAsync(session, source, batch, _hooks, ct), ct).ConfigureAwait(false);
        LogImported(logger, source, batch.Transactions.Count, summary.Added, summary.Updated, summary.DuplicatesSkipped, summary.MatchedToExisting, summary.TransfersMatched);
        return summary;
    }

    /// <summary>
    /// The import of one batch inside an open unit of work (the migration importer runs several batches in one,
    /// ADR 0099). Saves through <paramref name="session"/>; the caller commits.
    /// </summary>
    internal static async Task<ImportSummary> ImportCoreAsync(
        LedgerSession session,
        TransactionSource source,
        ImportBatch batch,
        IReadOnlyList<IImportCategorizationHook> hooks,
        CancellationToken ct)
    {
        var db = session.Db;
        var plan = await ImportPlanner.PlanAsync(db, source, batch, hooks, isPreview: false, ct).ConfigureAwait(false);
        var account = plan.Account;
        var warnings = plan.Warnings;

        // Existing rows the import changes: matched rows and transfer partners.
        var touched = plan.Rows
            .Where(r => r.Action is ImportRowAction.Update or ImportRowAction.Match)
            .Select(r => r.Dedup.ExistingId!.Value)
            .Concat(plan.Rows.Where(r => r.IsPaired).Select(r => r.TransferPartnerId!.Value))
            .Distinct()
            .ToList();
        var tracked = new Dictionary<Guid, Transaction>();
        foreach (var chunk in touched.Chunk(500))
        {
            foreach (var t in await db.Transactions.Where(t => chunk.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false))
            {
                tracked[t.Id] = t;
            }
        }

        var created = new Dictionary<string, Payee>(StringComparer.Ordinal);
        var inserts = new List<Transaction>();
        int added = 0, updated = 0, matched = 0, transfers = 0, uncategorized = 0;
        foreach (var row in plan.Rows)
        {
            var incoming = row.Incoming;
            switch (row.Action)
            {
                case ImportRowAction.Insert:
                    var draft = row.Draft!;
                    var txn = new Transaction
                    {
                        Id = row.NewId,
                        AccountId = account.Id,
                        Date = incoming.Date,
                        Amount = incoming.Amount,
                        PayeeRaw = incoming.PayeeRaw,
                        PayeeId = row.PayeeId,
                        Memo = string.IsNullOrWhiteSpace(draft.Memo) ? null : draft.Memo.Trim(),
                        CategoryId = draft.CategoryId,
                        Status = incoming.Status ?? (source == TransactionSource.Manual || incoming.IsPending ? TransactionStatus.Uncleared : TransactionStatus.Cleared),
                        IsApproved = draft.IsApproved ?? incoming.IsApproved ?? source == TransactionSource.Manual,
                        Source = source,
                        ProviderTransactionId = incoming.ProviderTransactionId,
                        ImportFingerprint = row.Dedup.Fingerprint,
                        CreatedAt = session.UtcNow,
                        UpdatedAt = session.UtcNow,
                    };
                    if (row.NewPayeeName is { } newPayee && !row.IsPaired)
                    {
                        txn.PayeeId = PayeeFor(newPayee).Id;
                    }

                    inserts.Add(txn);
                    added++;
                    var requiresCategory = account.IsOnBudget;
                    if (row.IsPaired)
                    {
                        var partner = tracked[row.TransferPartnerId!.Value];
                        var partnerOnBudget = plan.OnBudgetByAccount[partner.AccountId];
                        PairTransfer(txn, partner, account.IsOnBudget, partnerOnBudget);
                        requiresCategory = TransferRules.SideRequiresCategory(account.IsOnBudget, partnerOnBudget);
                        transfers++;
                    }

                    if (requiresCategory && txn.CategoryId is null)
                    {
                        uncategorized++;
                    }

                    break;

                case ImportRowAction.Update:
                    var existing = tracked[row.Dedup.ExistingId!.Value];
                    if (await UpdateAsync(db, existing, row, ct).ConfigureAwait(false))
                    {
                        updated++;
                    }
                    else
                    {
                        warnings.Add(new(ImportWarningCode.ReconciledNotUpdated, "A reconciled transaction was not changed by the import."));
                    }

                    break;

                case ImportRowAction.Match:
                    var manual = tracked[row.Dedup.ExistingId!.Value];
                    manual.ProviderTransactionId ??= incoming.ProviderTransactionId;
                    manual.ImportFingerprint = row.Dedup.Fingerprint;
                    manual.HasImportMatch = true;
                    if (!incoming.IsPending && manual.Status == TransactionStatus.Uncleared)
                    {
                        manual.Status = TransactionStatus.Cleared;
                    }

                    matched++;
                    break;
            }
        }

        if (batch.ReportedBalance is { } balance)
        {
            await RecordBalanceAsync(db, account.Id, balance, ct).ConfigureAwait(false);
        }

        // Tracked changes first (new payees, matched rows, transfer partners, balance), then the new
        // rows in bulk (ADR 0052): same transaction, audit rows and undo entry as a tracked insert.
        await session.SaveAsync(ct).ConfigureAwait(false);
        await BulkLedgerInsert.InsertAsync(session, inserts, ct).ConfigureAwait(false);
        await TagInsertsAsync(session, plan, ct).ConfigureAwait(false);

        return new ImportSummary(
            added,
            updated,
            plan.Rows.Count(r => r.Outcome == DedupOutcome.DuplicateSkipped && !r.Include),
            matched,
            transfers,
            uncategorized)
        {
            SkippedByChoice = plan.Rows.Count(r => r.IncludeByDefault && !r.Include),
            Warnings = warnings,
            ReportedBalance = batch.ReportedBalance,
        };

        // New payees are created only for rows that keep one (not transfers), once per name.
        Payee PayeeFor(string name)
        {
            var key = PayeeNames.Normalize(name);
            if (!created.TryGetValue(key, out var payee))
            {
                payee = new Payee { Name = name, NormalizedName = key };
                db.Payees.Add(payee);
                created[key] = payee;
            }

            return payee;
        }
    }

    // Tags the source stated for new rows (a Monarch export's Tags; M9): existing tags by name, ignoring
    // case, else new ones; written after the rows they reference, in the same unit of work.
    private static async Task TagInsertsAsync(LedgerSession session, ImportPlan plan, CancellationToken ct)
    {
        var tagged = plan.Rows.Where(r => r.Action == ImportRowAction.Insert && r.Incoming.Tags.Count > 0).ToList();
        if (tagged.Count == 0)
        {
            return;
        }

        var db = session.Db;
        var tags = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in await db.Tags.ToListAsync(ct).ConfigureAwait(false))
        {
            tags.TryAdd(tag.Name, tag);
        }

        foreach (var row in tagged)
        {
            foreach (var name in row.Incoming.Tags.Select(PayeeNames.Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!tags.TryGetValue(name, out var tag))
                {
                    tag = new Tag { Name = name.Length > 100 ? name[..100] : name };
                    db.Tags.Add(tag);
                    tags[name] = tag;
                }

                db.TransactionTags.Add(new TransactionTag { TransactionId = row.NewId, TagId = tag.Id });
            }
        }

        await session.SaveAsync(ct).ConfigureAwait(false);
    }

    // A transfer pair per ITransactionService: shared pair id, each side points at the other
    // account, no payee; only the on-budget side of an on/off-budget transfer keeps a category.
    private static void PairTransfer(Transaction txn, Transaction partner, bool txnOnBudget, bool partnerOnBudget)
    {
        var pairId = EntityIds.New();
        var category = txn.CategoryId ?? partner.CategoryId;
        txn.TransferPairId = pairId;
        txn.TransferAccountId = partner.AccountId;
        txn.PayeeId = null;
        txn.CategoryId = TransferRules.SideRequiresCategory(txnOnBudget, partnerOnBudget) ? category : null;
        partner.TransferPairId = pairId;
        partner.TransferAccountId = txn.AccountId;
        partner.PayeeId = null;
        partner.CategoryId = TransferRules.SideRequiresCategory(partnerOnBudget, txnOnBudget) ? category : null;
    }

    // PRD 6.5 steps 1 and 2: amount, date, payee text and pending state in place; a posted row
    // takes over its pending row's identity (its provider id). The pending id itself is only a
    // lookup key and is not stored, as in the matcher's reference model (ADR 0005). Reconciled
    // rows keep their amount and date.
    private static async Task<bool> UpdateAsync(KeelDbContext db, Transaction existing, PlannedRow row, CancellationToken ct)
    {
        var incoming = row.Incoming;
        if (existing.Status == TransactionStatus.Reconciled && (existing.Date != incoming.Date || existing.Amount != incoming.Amount))
        {
            return false;
        }

        existing.Date = incoming.Date;
        existing.Amount = incoming.Amount;
        if (!string.IsNullOrWhiteSpace(incoming.PayeeRaw))
        {
            existing.PayeeRaw = incoming.PayeeRaw;
        }

        existing.ImportFingerprint = row.Dedup.Fingerprint;
        existing.ProviderTransactionId = incoming.ProviderTransactionId ?? existing.ProviderTransactionId;

        if (!incoming.IsPending && existing.Status == TransactionStatus.Uncleared)
        {
            existing.Status = TransactionStatus.Cleared;
        }

        if (existing.TransferPairId is { } pairId)
        {
            var pair = await db.Transactions.SingleOrDefaultAsync(t => t.TransferPairId == pairId && t.Id != existing.Id, ct).ConfigureAwait(false);
            if (pair is not null && pair.Status != TransactionStatus.Reconciled)
            {
                pair.Date = existing.Date;
                pair.Amount = TransferRules.CounterpartAmount(existing.Amount);
            }
        }

        return true;
    }

    // F-TXN-3 / F-ACC-7: the source's balance is a dated snapshot and the account's reported balance.
    private static async Task RecordBalanceAsync(KeelDbContext db, Guid accountId, StatementBalance balance, CancellationToken ct)
    {
        var snapshot = await db.BalanceSnapshots.SingleOrDefaultAsync(s => s.AccountId == accountId && s.Date == balance.Date, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            snapshot = new BalanceSnapshot { AccountId = accountId, Date = balance.Date };
            db.BalanceSnapshots.Add(snapshot);
        }

        snapshot.Balance = balance.Balance;
        snapshot.Source = BalanceSource.Provider;

        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
        var at = balance.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (account.ReportedBalanceAt is not { } previous || previous <= at)
        {
            account.ReportedBalance = balance.Balance;
            account.ReportedBalanceAt = at;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Import from {Source}: {Rows} rows, {Added} added, {Updated} updated, {Duplicates} duplicates, {Matched} matched, {Transfers} transfers")]
    private static partial void LogImported(ILogger logger, TransactionSource source, int rows, int added, int updated, int duplicates, int matched, int transfers);
}
