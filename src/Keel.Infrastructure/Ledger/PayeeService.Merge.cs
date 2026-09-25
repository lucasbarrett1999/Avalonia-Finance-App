using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Rules;
using Keel.Application.Undo;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Domain.Rules;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Payee merge (F-TXN-9, ADR 0097).</summary>
public sealed partial class PayeeService
{
    /// <inheritdoc />
    public Task<PayeeMergePreview> PreviewMergeAsync(IReadOnlyCollection<Guid> payeeIds, Guid survivorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payeeIds);
        return Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var (survivor, merged) = await LoadMergeAsync(db, payeeIds, survivorId, ct).ConfigureAwait(false);
                    var ids = merged.Select(p => p.Id).ToList();
                    var transactions = await db.Transactions.CountAsync(t => t.PayeeId != null && ids.Contains(t.PayeeId.Value), ct).ConfigureAwait(false);
                    var scheduled = await db.ScheduledTransactions.CountAsync(s => ids.Contains(s.PayeeId), ct).ConfigureAwait(false);
                    var recurring = await db.RecurringItems.CountAsync(r => ids.Contains(r.PayeeId), ct).ConfigureAwait(false);
                    var names = merged.Select(p => p.NormalizedName).ToHashSet(StringComparer.Ordinal);
                    var rules = await RuleRewriter.CountAsync(
                        db,
                        c => NamesPayee(c, names),
                        a => a is SetPayeeAction set && names.Contains(PayeeNames.Normalize(set.Payee)),
                        ct).ConfigureAwait(false);
                    return new PayeeMergePreview(
                        Dto(survivor),
                        merged.Select(Dto).ToList(),
                        transactions,
                        scheduled,
                        recurring,
                        rules,
                        survivor.DefaultCategoryId ?? merged.Select(p => p.DefaultCategoryId).FirstOrDefault(c => c is not null));
                }
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<PayeeMergeResult> MergeAsync(IReadOnlyCollection<Guid> payeeIds, Guid survivorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payeeIds);
        var result = await writer.RunAsync(
            LedgerAction.MergePayees,
            async session =>
            {
                var db = session.Db;
                var (survivor, merged) = await LoadMergeAsync(db, payeeIds, survivorId, ct).ConfigureAwait(false);
                var counts = await MergeCoreAsync(db, survivor, merged, ct).ConfigureAwait(false);
                return new PayeeMergeResult(Dto(survivor), merged.Count, counts.Transactions, counts.Scheduled, counts.Recurring, counts.Rules);
            },
            ct).ConfigureAwait(false);
        if (result.Rules > 0)
        {
            bus.Publish(new RulesChanged());
        }

        return result;
    }

    /// <summary>
    /// Moves everything that refers to <paramref name="merged"/> to <paramref name="survivor"/> and removes the
    /// merged payees, as tracked changes of the caller's ledger action (so the learner replays them, ADR 0027).
    /// </summary>
    internal static async Task<MergeCounts> MergeCoreAsync(KeelDbContext db, Payee survivor, IReadOnlyList<Payee> merged, CancellationToken ct)
    {
        var ids = merged.Select(p => p.Id).ToList();

        // Deleted transactions too: a restored row must not point at a removed payee.
        var transactions = await db.Transactions.IgnoreQueryFilters().Where(t => t.PayeeId != null && ids.Contains(t.PayeeId.Value)).ToListAsync(ct).ConfigureAwait(false);
        transactions.ForEach(t => t.PayeeId = survivor.Id);
        var scheduled = await db.ScheduledTransactions.Where(s => ids.Contains(s.PayeeId)).ToListAsync(ct).ConfigureAwait(false);
        scheduled.ForEach(s => s.PayeeId = survivor.Id);
        var recurring = await db.RecurringItems.Where(r => ids.Contains(r.PayeeId)).ToListAsync(ct).ConfigureAwait(false);
        recurring.ForEach(r => r.PayeeId = survivor.Id);
        survivor.DefaultCategoryId ??= merged.Select(p => p.DefaultCategoryId).FirstOrDefault(c => c is not null);
        var rules = await RenamePayeeInRulesAsync(db, merged.Select(p => p.Name).ToList(), survivor.Name, ct).ConfigureAwait(false);
        db.Payees.RemoveRange(merged);
        return new MergeCounts(transactions.Count(t => !t.IsDeleted), scheduled.Count, recurring.Count, rules);
    }

    // Rules name payees by text: "set payee to X" and "payee equals X" follow a rename or merge of X.
    private static Task<int> RenamePayeeInRulesAsync(KeelDbContext db, IReadOnlyList<string> from, string to, CancellationToken ct)
    {
        var names = from.Select(PayeeNames.Normalize).ToHashSet(StringComparer.Ordinal);
        return RuleRewriter.RewriteAsync(
            db,
            c => c is PayeeCondition payee && NamesPayee(payee, names) ? payee with { Value = to } : c,
            a => a is SetPayeeAction set && names.Contains(PayeeNames.Normalize(set.Payee)) ? set with { Payee = to } : a,
            ct);
    }

    private static bool NamesPayee(RuleCondition condition, IReadOnlySet<string> normalizedNames) =>
        condition is PayeeCondition { Operator: TextOperator.EqualTo } payee && normalizedNames.Contains(PayeeNames.Normalize(payee.Value));

    // The survivor and the other payees, in the order given; refuses a merge with nothing to merge.
    private static async Task<(Payee Survivor, List<Payee> Merged)> LoadMergeAsync(KeelDbContext db, IReadOnlyCollection<Guid> payeeIds, Guid survivorId, CancellationToken ct)
    {
        var ids = payeeIds.Append(survivorId).Distinct().ToList();
        var payees = await db.Payees.Where(p => ids.Contains(p.Id)).ToListAsync(ct).ConfigureAwait(false);
        if (payees.Count != ids.Count)
        {
            throw new LedgerValidationException(LedgerError.PayeeNotFound);
        }

        var survivor = payees.Single(p => p.Id == survivorId);
        var merged = payeeIds.Where(id => id != survivorId).Distinct().Select(id => payees.Single(p => p.Id == id)).ToList();
        if (merged.Count == 0 || payees.Any(p => p.IsTransferPayeeForAccountId is not null))
        {
            throw new LedgerValidationException(LedgerError.PayeeMergeInvalid);
        }

        return (survivor, merged);
    }

    private static PayeeDto Dto(Payee payee) => new(payee.Id, payee.Name, payee.DefaultCategoryId);

    /// <summary>What a merge moved.</summary>
    internal readonly record struct MergeCounts(int Transactions, int Scheduled, int Recurring, int Rules);
}
