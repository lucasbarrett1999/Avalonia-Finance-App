using System.Globalization;
using Keel.Application.Rules;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Domain.Rules;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Rules;

/// <summary>
/// What will really be written for one transaction: the rule engine's result reduced to the
/// changes the ledger accepts (ADR 0026). Preview and apply both use <see cref="MutationPlanner.Plan"/>,
/// so they agree for the same ledger state.
/// </summary>
/// <param name="Before">The transaction as stored.</param>
/// <param name="After">The transaction after the applicable changes.</param>
/// <param name="Changes">Fields that change.</param>
/// <param name="RuleNames">Rules that matched, in order.</param>
internal sealed record PlannedMutation(TransactionSnapshot Before, TransactionSnapshot After, RuleChanges Changes, IReadOnlyList<string> RuleNames)
{
    /// <summary>Transaction id.</summary>
    public Guid TransactionId => Before.Id;

    /// <summary>For a new transfer: the category of the counterpart row (when that side needs one).</summary>
    public Guid? PairCategoryId { get; init; }
}

/// <summary>Plans rule mutations and writes them onto tracked entities inside a ledger session.</summary>
internal static class MutationPlanner
{
    /// <summary>
    /// Reduces a rule run to what the ledger accepts: no payee on transfers, no splits on
    /// transfers, a category on a transfer only where <see cref="TransferRules.SideRequiresCategory"/>
    /// allows it, and a new transfer only to another existing, open account from an unsplit,
    /// non-transfer row (with a category when the transfer needs one).
    /// </summary>
    public static PlannedMutation Plan(RuleApplication application, SnapshotSource source)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(source);
        var before = application.Original;
        var after = application.Result;
        var names = application.Trace.Matched.Select(m => m.RuleName).ToList();
        if (!application.Mutations.HasChanges)
        {
            return new PlannedMutation(before, before, RuleChanges.None, names);
        }

        var ownOnBudget = source.Accounts.TryGetValue(before.AccountId, out var own) && own.IsOnBudget;

        // A new transfer.
        if (after.TransferAccountId != before.TransferAccountId)
        {
            var ok = before.TransferAccountId is null
                && !before.IsSplit
                && !after.IsSplit
                && after.TransferAccountId is { } otherId
                && otherId != before.AccountId
                && source.Accounts.TryGetValue(otherId, out var other)
                && !other.IsClosed
                && (!TransferRules.TransferRequiresCategory(ownOnBudget, other.IsOnBudget) || (after.CategoryId ?? before.CategoryId) is not null);
            if (!ok)
            {
                after = after with { TransferAccountId = before.TransferAccountId };
            }
        }

        Guid? pairCategory = null;
        if (after.TransferAccountId is { } target)
        {
            var otherOnBudget = source.Accounts.TryGetValue(target, out var other) && other.IsOnBudget;
            var sideRequires = TransferRules.SideRequiresCategory(ownOnBudget, otherOnBudget);
            Guid? category;
            if (before.TransferAccountId is null)
            {
                // New transfer: the category goes to whichever side needs one (as the ledger service does).
                var transferCategory = after.CategoryId ?? before.CategoryId;
                category = sideRequires ? transferCategory : null;
                pairCategory = TransferRules.SideRequiresCategory(otherOnBudget, ownOnBudget) ? transferCategory : null;
            }
            else
            {
                category = sideRequires ? after.CategoryId : before.CategoryId;
            }

            after = after with { Payee = before.Payee, Splits = before.Splits, CategoryId = category };
        }

        if (string.IsNullOrWhiteSpace(after.Payee))
        {
            after = after with { Payee = before.Payee };
        }

        if (after.IsSplit)
        {
            after = after with { CategoryId = null };
        }

        return new PlannedMutation(before, after, Diff(before, after), names) { PairCategoryId = pairCategory };
    }

    /// <summary>Fields that differ between two snapshots of the same transaction.</summary>
    public static RuleChanges Diff(TransactionSnapshot before, TransactionSnapshot after)
    {
        var changes = RuleChanges.None;
        if (!string.Equals(before.Payee, after.Payee, StringComparison.Ordinal))
        {
            changes |= RuleChanges.Payee;
        }

        if (before.CategoryId != after.CategoryId)
        {
            changes |= RuleChanges.Category;
        }

        if (!string.Equals(before.Memo ?? string.Empty, after.Memo ?? string.Empty, StringComparison.Ordinal))
        {
            changes |= RuleChanges.Memo;
        }

        if (after.Tags.Any(t => !before.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            changes |= RuleChanges.Tags;
        }

        if (after.IsApproved && !before.IsApproved)
        {
            changes |= RuleChanges.Approved;
        }

        if (after.IsFlagged && !before.IsFlagged)
        {
            changes |= RuleChanges.Flagged;
        }

        // Stored splits have no order, so compare them as a multiset.
        if (!SameSplits(before.Splits, after.Splits))
        {
            changes |= RuleChanges.Splits;
        }

        if (before.TransferAccountId != after.TransferAccountId)
        {
            changes |= RuleChanges.TransferAccount;
        }

        return changes;
    }

    /// <summary>Display values of each changed field.</summary>
    public static IReadOnlyList<FieldChange> Describe(PlannedMutation plan, SnapshotSource source)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        var (before, after) = (plan.Before, plan.After);
        var list = new List<FieldChange>();
        foreach (var field in Enum.GetValues<RuleChanges>().Where(f => f != RuleChanges.None && (plan.Changes & f) == f))
        {
            list.Add(field switch
            {
                RuleChanges.Payee => new FieldChange(field, NullIfBlank(before.EffectivePayee), NullIfBlank(after.EffectivePayee)),
                RuleChanges.Category => new FieldChange(field, CategoryName(before.CategoryId, source), CategoryName(after.CategoryId, source)),
                RuleChanges.Memo => new FieldChange(field, NullIfBlank(before.Memo), NullIfBlank(after.Memo)),
                RuleChanges.Tags => new FieldChange(field, Join(before.Tags), Join(after.Tags)),
                RuleChanges.Splits => new FieldChange(field, SplitText(before, source), SplitText(after, source)),
                RuleChanges.TransferAccount => new FieldChange(field, AccountName(before.TransferAccountId, source), AccountName(after.TransferAccountId, source)),
                _ => new FieldChange(field, null, null),
            });
        }

        return list;
    }

    /// <summary>The preview row of a plan.</summary>
    public static RuleOutcomePreview Preview(PlannedMutation plan, SnapshotSource source) => new(
        plan.TransactionId,
        source.Accounts.TryGetValue(plan.Before.AccountId, out var account) ? account.Name : string.Empty,
        plan.Before.Date,
        plan.Before.EffectivePayee,
        plan.Before.Amount,
        plan.Before.Currency,
        plan.Changes,
        Describe(plan, source),
        plan.RuleNames);

    /// <summary>
    /// Writes a plan onto the tracked <paramref name="transaction"/> (loaded with its splits):
    /// payee (get-or-create), category, memo, splits (replaced), approval, tags and the flag
    /// (tag rows), and a new transfer with its counterpart row.
    /// </summary>
    public static async Task WriteAsync(KeelDbContext db, Transaction transaction, PlannedMutation plan, SnapshotSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(plan);
        var after = plan.After;
        var changes = plan.Changes;

        if ((changes & RuleChanges.Payee) != 0)
        {
            var payee = await LedgerLookups.GetOrAddPayeeAsync(db, after.Payee, ct).ConfigureAwait(false);
            transaction.PayeeId = payee?.Id;
            if (string.IsNullOrWhiteSpace(transaction.PayeeRaw) && payee is not null)
            {
                transaction.PayeeRaw = payee.Name;
            }
        }

        if ((changes & RuleChanges.Memo) != 0)
        {
            transaction.Memo = string.IsNullOrWhiteSpace(after.Memo) ? null : after.Memo.Trim();
        }

        if ((changes & RuleChanges.Splits) != 0)
        {
            if (transaction.Splits.Count > 0)
            {
                db.TransactionSplits.RemoveRange(transaction.Splits);
                transaction.Splits.Clear();
            }

            foreach (var line in after.Splits)
            {
                db.TransactionSplits.Add(new TransactionSplit
                {
                    TransactionId = transaction.Id,
                    CategoryId = line.CategoryId,
                    Memo = string.IsNullOrWhiteSpace(line.Memo) ? null : line.Memo.Trim(),
                    Amount = line.Amount,
                    TransferAccountId = line.TransferAccountId,
                });
            }
        }

        if ((changes & (RuleChanges.Category | RuleChanges.Splits)) != 0)
        {
            transaction.CategoryId = after.IsSplit ? null : after.CategoryId;
        }

        if ((changes & RuleChanges.Approved) != 0)
        {
            transaction.IsApproved = true;
        }

        if ((changes & RuleChanges.Tags) != 0)
        {
            foreach (var tag in after.Tags.Where(t => !plan.Before.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                await AddTagAsync(db, transaction.Id, tag, ct).ConfigureAwait(false);
            }
        }

        if ((changes & RuleChanges.Flagged) != 0)
        {
            await AddTagAsync(db, transaction.Id, SnapshotSource.FlaggedTag, ct).ConfigureAwait(false);
        }

        if ((changes & RuleChanges.TransferAccount) != 0 && after.TransferAccountId is { } otherId)
        {
            var pairKey = EntityIds.New();
            transaction.TransferPairId = pairKey;
            transaction.TransferAccountId = otherId;
            transaction.PayeeId = null;
            transaction.CategoryId = after.CategoryId;
            db.Transactions.Add(new Transaction
            {
                AccountId = otherId,
                TransferAccountId = transaction.AccountId,
                TransferPairId = pairKey,
                Date = transaction.Date,
                Amount = TransferRules.CounterpartAmount(transaction.Amount),
                Memo = transaction.Memo,
                Status = TransactionStatus.Uncleared,
                IsApproved = true,
                Source = transaction.Source == TransactionSource.System ? TransactionSource.Manual : transaction.Source,
                CategoryId = plan.PairCategoryId,
            });
        }
    }

    // Tag names compare case-insensitively everywhere; the tag service owns the lookup (F-TXN-8).
    private static Task AddTagAsync(KeelDbContext db, Guid transactionId, string name, CancellationToken ct) =>
        Domain.Ledger.TagNames.Clean(name).Length == 0 ? Task.CompletedTask : Tags.TagService.AddToTransactionAsync(db, transactionId, name, ct);

    private static bool SameSplits(IReadOnlyList<SnapshotSplit> a, IReadOnlyList<SnapshotSplit> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var remaining = b.ToList();
        foreach (var split in a)
        {
            var index = remaining.IndexOf(split);
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return true;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Join(IReadOnlyList<string> tags) => tags.Count == 0 ? null : string.Join(", ", tags);

    private static string? CategoryName(Guid? id, SnapshotSource source) =>
        id is { } value ? (source.Categories.TryGetValue(value, out var c) ? c.Name : null) : null;

    private static string? AccountName(Guid? id, SnapshotSource source) =>
        id is { } value ? (source.Accounts.TryGetValue(value, out var a) ? a.Name : null) : null;

    private static string? SplitText(TransactionSnapshot snapshot, SnapshotSource source)
    {
        if (!snapshot.IsSplit)
        {
            return CategoryName(snapshot.CategoryId, source);
        }

        return string.Join("; ", snapshot.Splits.Select(s =>
            (CategoryName(s.CategoryId, source) ?? "?") + " " + new Money(Math.Abs(s.Amount), snapshot.Currency).Format(CultureInfo.CurrentCulture)));
    }
}
