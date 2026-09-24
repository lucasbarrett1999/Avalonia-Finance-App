using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Transactions, transfers, splits and reconciliation (F-ACC-2..5).</summary>
public sealed class TransactionService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer) : ITransactionService
{
    /// <inheritdoc />
    public Task<TransactionDto?> GetAsync(Guid id, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var txn = await db.Transactions.IgnoreQueryFilters().AsNoTracking().Include(t => t.Splits)
                    .SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
                if (txn is null)
                {
                    return null;
                }

                var payee = txn.PayeeId is { } payeeId
                    ? await db.Payees.AsNoTracking().Where(p => p.Id == payeeId).Select(p => p.Name).SingleOrDefaultAsync(ct).ConfigureAwait(false)
                    : null;
                return ToDto(txn, payee);
            }
        },
        ct);

    /// <inheritdoc />
    public async Task<TransactionDto> SaveAsync(SaveTransactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var splits = request.Splits ?? [];
        if (splits.Count > 0)
        {
            if (request.TransferAccountId is not null)
            {
                throw new LedgerValidationException(LedgerError.SplitTransfer);
            }

            if (SplitRules.Validate(request.Amount, splits.Select(s => s.Amount).ToList()) is { } problem)
            {
                throw new LedgerValidationException(LedgerLookups.ToError(problem));
            }
        }

        if (request.TransferAccountId == request.AccountId)
        {
            throw new LedgerValidationException(LedgerError.TransferToSameAccount);
        }

        var id = await writer.RunAsync(
            request.Id is null ? LedgerAction.AddTransaction : LedgerAction.EditTransaction,
            session => SaveCoreAsync(session, request, splits, ct),
            ct).ConfigureAwait(false);
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return writer.RunAsync(
            LedgerAction.DeleteTransactions,
            async session =>
            {
                var rows = await WithPairsAsync(session.Db.Transactions, ids, ct).ConfigureAwait(false);
                if (rows.Any(t => t.Status == TransactionStatus.Reconciled))
                {
                    throw new LedgerValidationException(LedgerError.ReconciledLocked);
                }

                foreach (var row in rows)
                {
                    row.IsDeleted = true;
                }

                return rows.Count(r => ids.Contains(r.Id));
            },
            ct);
    }

    /// <inheritdoc />
    public Task<int> RestoreAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return writer.RunAsync(
            LedgerAction.RestoreTransactions,
            async session =>
            {
                var rows = await WithPairsAsync(session.Db.Transactions.IgnoreQueryFilters().Where(t => t.IsDeleted), ids, ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    row.IsDeleted = false;
                }

                return rows.Count(r => ids.Contains(r.Id));
            },
            ct);
    }

    /// <inheritdoc />
    public Task<int> PurgeDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return writer.RunAsync(
            LedgerAction.PurgeTransactions,
            async session =>
            {
                var deleted = session.Db.Transactions.IgnoreQueryFilters().Include(t => t.Splits).Where(t => t.IsDeleted);
                var rows = await WithPairsAsync(deleted, ids, ct).ConfigureAwait(false);
                session.Db.Transactions.RemoveRange(rows);
                return rows.Count(r => ids.Contains(r.Id));
            },
            ct);
    }

    /// <inheritdoc />
    public Task<int> SetClearedAsync(IReadOnlyCollection<Guid> ids, bool cleared, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var target = cleared ? TransactionStatus.Cleared : TransactionStatus.Uncleared;
        return writer.RunAsync(
            LedgerAction.ChangeCleared,
            async session =>
            {
                var rows = await session.Db.Transactions.Where(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);
                var changed = 0;
                foreach (var row in rows.Where(r => r.Status != TransactionStatus.Reconciled && r.Status != target))
                {
                    row.Status = target;
                    changed++;
                }

                return changed;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<TransactionStatus> ToggleClearedAsync(Guid id, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.ChangeCleared,
            async session =>
            {
                var row = await session.Db.Transactions.SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.TransactionNotFound);
                row.Status = row.Status switch
                {
                    TransactionStatus.Uncleared => TransactionStatus.Cleared,
                    TransactionStatus.Cleared => TransactionStatus.Uncleared,
                    _ => throw new LedgerValidationException(LedgerError.ReconciledLocked),
                };
                return row.Status;
            },
            ct);

    /// <inheritdoc />
    public Task<int> ApproveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return writer.RunAsync(
            LedgerAction.Approve,
            async session =>
            {
                var rows = await session.Db.Transactions.Where(t => ids.Contains(t.Id) && !t.IsApproved).ToListAsync(ct).ConfigureAwait(false);
                rows.ForEach(r => r.IsApproved = true);
                return rows.Count;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<int> CategorizeAsync(IReadOnlyCollection<Guid> ids, Guid categoryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return writer.RunAsync(
            LedgerAction.Categorize,
            async session =>
            {
                var db = session.Db;
                await LedgerLookups.EnsureCategoryAsync(db, categoryId, ct).ConfigureAwait(false);
                var rows = await db.Transactions.Include(t => t.Splits).Where(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);
                var budgetFlags = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.IsOnBudget, ct).ConfigureAwait(false);
                var changed = 0;
                foreach (var row in rows)
                {
                    if (row.IsSplit)
                    {
                        continue;
                    }

                    if (row.TransferAccountId is { } other
                        && !TransferRules.SideRequiresCategory(budgetFlags[row.AccountId], budgetFlags[other]))
                    {
                        continue;
                    }

                    if (row.CategoryId != categoryId)
                    {
                        row.CategoryId = categoryId;
                        changed++;
                    }
                }

                return changed;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<int> MoveToAccountAsync(IReadOnlyCollection<Guid> ids, Guid accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return writer.RunAsync(
            LedgerAction.MoveTransactions,
            async session =>
            {
                var db = session.Db;
                var target = await LedgerLookups.OpenAccountAsync(db, accountId, ct).ConfigureAwait(false);
                var budgetFlags = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.IsOnBudget, ct).ConfigureAwait(false);
                var rows = await db.Transactions.Where(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);
                if (rows.Any(r => r.Status == TransactionStatus.Reconciled && r.AccountId != accountId))
                {
                    throw new LedgerValidationException(LedgerError.ReconciledLocked);
                }

                var moved = 0;
                foreach (var row in rows.Where(r => r.AccountId != accountId && r.TransferAccountId != accountId))
                {
                    row.AccountId = target.Id;
                    moved++;
                    if (row.TransferPairId is not { } pairId)
                    {
                        continue;
                    }

                    var pair = await db.Transactions.SingleOrDefaultAsync(t => t.TransferPairId == pairId && t.Id != row.Id, ct).ConfigureAwait(false);
                    if (pair is null)
                    {
                        continue;
                    }

                    pair.TransferAccountId = target.Id;
                    ApplyTransferCategories(row, pair, budgetFlags[row.AccountId], budgetFlags[pair.AccountId], row.CategoryId ?? pair.CategoryId);
                }

                return moved;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<ReconciliationStatus> GetReconciliationStatusAsync(Guid accountId, DateOnly statementDate, long statementBalance, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await StatusAsync(db, accountId, statementDate, statementBalance, ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public Task<ReconciliationResult> FinishReconciliationAsync(FinishReconciliationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return writer.RunAsync(
            LedgerAction.Reconcile,
            async session =>
            {
                var db = session.Db;
                var account = await LedgerLookups.OpenAccountAsync(db, request.AccountId, ct).ConfigureAwait(false);
                var status = await StatusAsync(db, account.Id, request.StatementDate, request.StatementBalance, ct).ConfigureAwait(false);
                Transaction? adjustment = null;
                if (status.Difference != 0)
                {
                    if (!request.CreateAdjustment)
                    {
                        throw new LedgerValidationException(LedgerError.ReconciliationNotBalanced);
                    }

                    var payee = await LedgerLookups.GetOrAddPayeeAsync(db, LedgerLookups.ReconciliationAdjustmentPayee, ct).ConfigureAwait(false);
                    adjustment = new Transaction
                    {
                        AccountId = account.Id,
                        Date = request.StatementDate,
                        PayeeId = payee!.Id,
                        PayeeRaw = LedgerLookups.ReconciliationAdjustmentPayee,
                        Amount = status.Difference,
                        CategoryId = LedgerLookups.SystemInflowCategory(account),
                        Status = TransactionStatus.Reconciled,
                        IsApproved = true,
                        Source = TransactionSource.System,
                    };
                    db.Transactions.Add(adjustment);
                }

                var toLock = await db.Transactions
                    .Where(t => t.AccountId == account.Id && t.Status == TransactionStatus.Cleared && t.Date <= request.StatementDate)
                    .ToListAsync(ct).ConfigureAwait(false);
                toLock.ForEach(t => t.Status = TransactionStatus.Reconciled);

                var reconciliation = new Reconciliation
                {
                    AccountId = account.Id,
                    StatementDate = request.StatementDate,
                    StatementBalance = request.StatementBalance,
                    CompletedAt = session.UtcNow,
                };
                db.Reconciliations.Add(reconciliation);
                return new ReconciliationResult(reconciliation.Id, toLock.Count + (adjustment is null ? 0 : 1), adjustment?.Id, status.Difference);
            },
            ct);
    }

    private static async Task<ReconciliationStatus> StatusAsync(KeelDbContext db, Guid accountId, DateOnly statementDate, long statementBalance, CancellationToken ct)
    {
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Date <= statementDate)
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Sum = g.Sum(t => t.Amount), Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        var cleared = rows.Where(r => r.Status != TransactionStatus.Uncleared).Sum(r => r.Sum);
        return new ReconciliationStatus(
            accountId,
            statementDate,
            statementBalance,
            cleared,
            ReconciliationMath.Difference(cleared, statementBalance),
            rows.Where(r => r.Status == TransactionStatus.Cleared).Sum(r => r.Count),
            rows.Where(r => r.Status == TransactionStatus.Uncleared).Sum(r => r.Count));
    }

    internal static async Task<Guid> SaveCoreAsync(LedgerSession session, SaveTransactionRequest request, IReadOnlyList<SplitLine> splits, CancellationToken ct)
    {
        var db = session.Db;
        var account = await LedgerLookups.OpenAccountAsync(db, request.AccountId, ct).ConfigureAwait(false);
        Account? other = request.TransferAccountId is { } otherId
            ? await LedgerLookups.OpenAccountAsync(db, otherId, ct).ConfigureAwait(false)
            : null;

        foreach (var categoryId in splits.Select(s => s.CategoryId).Append(request.CategoryId).Distinct())
        {
            await LedgerLookups.EnsureCategoryAsync(db, categoryId, ct).ConfigureAwait(false);
        }

        Transaction txn;
        Transaction? pair = null;
        if (request.Id is { } id)
        {
            txn = await db.Transactions.Include(t => t.Splits).SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false)
                ?? throw new LedgerValidationException(LedgerError.TransactionNotFound);
            if (txn.TransferPairId is { } pairId)
            {
                pair = await db.Transactions.SingleOrDefaultAsync(t => t.TransferPairId == pairId && t.Id != txn.Id, ct).ConfigureAwait(false);
            }

            if (IsLockedChange(txn, request.AccountId, request.Date, request.Amount)
                || (pair is not null && other is not null && IsLockedChange(pair, other.Id, request.Date, TransferRules.CounterpartAmount(request.Amount))))
            {
                throw new LedgerValidationException(LedgerError.ReconciledLocked);
            }

            if (pair is not null && other is null && pair.Status == TransactionStatus.Reconciled)
            {
                throw new LedgerValidationException(LedgerError.ReconciledLocked);
            }
        }
        else
        {
            txn = new Transaction { Source = TransactionSource.Manual };
            db.Transactions.Add(txn);
        }

        txn.AccountId = account.Id;
        txn.Date = request.Date;
        txn.Amount = request.Amount;
        txn.Memo = string.IsNullOrWhiteSpace(request.Memo) ? null : request.Memo.Trim();
        txn.IsApproved = request.IsApproved;
        if (txn.Status != TransactionStatus.Reconciled || request.Status != TransactionStatus.Reconciled)
        {
            txn.Status = request.Status == TransactionStatus.Reconciled && request.Id is null ? TransactionStatus.Cleared : request.Status;
        }

        // Splits are replaced wholesale; the split-sum triggers check the final state at commit.
        if (txn.Splits.Count > 0)
        {
            db.TransactionSplits.RemoveRange(txn.Splits);
            txn.Splits.Clear();
        }

        if (other is null)
        {
            if (pair is not null)
            {
                pair.IsDeleted = true;
            }

            txn.TransferAccountId = null;
            txn.TransferPairId = null;
            var payee = await LedgerLookups.GetOrAddPayeeAsync(db, request.Payee, ct).ConfigureAwait(false);
            txn.PayeeId = payee?.Id;
            txn.PayeeRaw = payee?.Name ?? string.Empty;
            txn.CategoryId = splits.Count > 0 ? null : request.CategoryId;
            foreach (var line in splits)
            {
                // Added explicitly: a client-generated key found through a navigation would be taken for an existing row.
                db.TransactionSplits.Add(new TransactionSplit
                {
                    TransactionId = txn.Id,
                    CategoryId = line.CategoryId,
                    Memo = string.IsNullOrWhiteSpace(line.Memo) ? null : line.Memo.Trim(),
                    Amount = line.Amount,
                });
            }

            return txn.Id;
        }

        if (TransferRules.TransferRequiresCategory(account.IsOnBudget, other.IsOnBudget) && request.CategoryId is null)
        {
            throw new LedgerValidationException(LedgerError.CategoryRequiredForTransfer);
        }

        if (pair is null)
        {
            pair = new Transaction
            {
                Source = txn.Source == TransactionSource.System ? TransactionSource.Manual : txn.Source,
                Status = TransactionStatus.Uncleared,
                IsApproved = true,
            };
            db.Transactions.Add(pair);
        }

        var pairKey = txn.TransferPairId ?? EntityIds.New();
        txn.TransferPairId = pairKey;
        txn.TransferAccountId = other.Id;
        txn.PayeeId = null;
        txn.PayeeRaw = string.Empty;

        pair.TransferPairId = pairKey;
        pair.AccountId = other.Id;
        pair.TransferAccountId = account.Id;
        pair.Date = request.Date;
        pair.Amount = TransferRules.CounterpartAmount(request.Amount);
        pair.Memo = txn.Memo;
        pair.PayeeId = null;
        pair.PayeeRaw = string.Empty;
        pair.IsDeleted = false;
        ApplyTransferCategories(txn, pair, account.IsOnBudget, other.IsOnBudget, request.CategoryId);
        return txn.Id;
    }

    private static void ApplyTransferCategories(Transaction side, Transaction pair, bool sideOnBudget, bool pairOnBudget, Guid? categoryId)
    {
        side.CategoryId = TransferRules.SideRequiresCategory(sideOnBudget, pairOnBudget) ? categoryId : null;
        pair.CategoryId = TransferRules.SideRequiresCategory(pairOnBudget, sideOnBudget) ? categoryId : null;
    }

    private static bool IsLockedChange(Transaction existing, Guid accountId, DateOnly date, long amount) =>
        existing.Status == TransactionStatus.Reconciled
        && (existing.AccountId != accountId || existing.Date != date || existing.Amount != amount);

    // Loads the rows with these ids plus the other side of any transfer among them.
    private static async Task<List<Transaction>> WithPairsAsync(IQueryable<Transaction> source, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await source.Where(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);
        var pairIds = rows.Where(r => r.TransferPairId is not null).Select(r => r.TransferPairId!.Value).Distinct().ToList();
        if (pairIds.Count > 0)
        {
            var known = rows.Select(r => r.Id).ToHashSet();
            var pairs = await source.Where(t => t.TransferPairId != null && pairIds.Contains(t.TransferPairId.Value)).ToListAsync(ct).ConfigureAwait(false);
            rows.AddRange(pairs.Where(p => !known.Contains(p.Id)));
        }

        return rows;
    }

    private static TransactionDto ToDto(Transaction txn, string? payeeName) => new(
        txn.Id,
        txn.AccountId,
        txn.Date,
        txn.PayeeId,
        payeeName ?? txn.PayeeRaw,
        txn.CategoryId,
        txn.Memo,
        txn.Amount,
        txn.Status,
        txn.IsApproved,
        txn.Source,
        txn.TransferAccountId,
        txn.TransferPairId,
        txn.Splits.Select(s => new SplitLine(s.CategoryId, s.Memo, s.Amount)).ToList(),
        txn.IsDeleted);
}
