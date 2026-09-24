using System.Runtime.CompilerServices;
using Keel.Application.Rules;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Rules;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Rules;

/// <summary>
/// Turns stored transactions into <see cref="TransactionSnapshot"/>s for the rule engine and the
/// learner: payee names, tags (the reserved <see cref="FlaggedTag"/> becomes
/// <see cref="TransactionSnapshot.IsFlagged"/>), splits and the account currency. Lookups are
/// loaded once per operation so large scans stream a single query.
/// </summary>
internal sealed class SnapshotSource
{
    /// <summary>The tag that stores a rule's "flag" action (ADR 0026).</summary>
    public const string FlaggedTag = "Flagged";

    private readonly Dictionary<Guid, string> _payees;
    private readonly ILookup<Guid, string> _tags;

    private SnapshotSource(
        Dictionary<Guid, string> payees,
        ILookup<Guid, string> tags,
        Dictionary<Guid, Account> accounts,
        Dictionary<Guid, Category> categories)
    {
        _payees = payees;
        _tags = tags;
        Accounts = accounts;
        Categories = categories;
    }

    /// <summary>Accounts by id (including closed ones).</summary>
    public Dictionary<Guid, Account> Accounts { get; }

    /// <summary>Categories by id.</summary>
    public Dictionary<Guid, Category> Categories { get; }

    /// <summary>Loads the lookups (payee names, tags, accounts, categories).</summary>
    public static async Task<SnapshotSource> LoadAsync(KeelDbContext db, CancellationToken ct)
    {
        var payees = await db.Payees.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct).ConfigureAwait(false);
        var tagRows = await (from tt in db.TransactionTags.AsNoTracking()
                             join g in db.Tags.AsNoTracking() on tt.TagId equals g.Id
                             select new { tt.TransactionId, g.Name }).ToListAsync(ct).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        return new SnapshotSource(payees, tagRows.ToLookup(r => r.TransactionId, r => r.Name), accounts, categories);
    }

    /// <summary>Names for rule descriptions.</summary>
    public RuleNames Names(string currency = Currency.Default) => new(
        Categories.ToDictionary(kv => kv.Key, kv => kv.Value.Name),
        Accounts.ToDictionary(kv => kv.Key, kv => kv.Value.Name),
        currency);

    /// <summary>The validation context (known categories and accounts).</summary>
    public RuleValidationContext ValidationContext() => new(Categories.Keys.ToHashSet(), Accounts.Keys.ToHashSet());

    /// <summary>The display name of a payee id.</summary>
    public string? PayeeName(Guid? id) => id is { } value && _payees.TryGetValue(value, out var name) ? name : null;

    /// <summary>The snapshot of a transaction whose splits are loaded.</summary>
    public TransactionSnapshot Snapshot(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var tags = _tags[transaction.Id].ToList();
        var flagged = tags.RemoveAll(t => string.Equals(t, FlaggedTag, StringComparison.OrdinalIgnoreCase)) > 0;
        tags.Sort(StringComparer.OrdinalIgnoreCase);
        var currency = Accounts.TryGetValue(transaction.AccountId, out var account) ? account.Currency : Currency.Default;
        return TransactionSnapshot.From(transaction, PayeeName(transaction.PayeeId), tags, currency) with { IsFlagged = flagged };
    }

    /// <summary>Non-deleted transactions in a retroactive scope, oldest first, without tracking.</summary>
    public static IQueryable<Transaction> InScope(KeelDbContext db, RetroactiveScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var query = db.Transactions.AsNoTracking().Include(t => t.Splits).AsQueryable();
        if (scope.From is { } from)
        {
            query = query.Where(t => t.Date >= from);
        }

        if (scope.To is { } to)
        {
            query = query.Where(t => t.Date <= to);
        }

        if (scope.AccountId is { } account)
        {
            query = query.Where(t => t.AccountId == account);
        }

        if (scope.UnapprovedOnly)
        {
            query = query.Where(t => !t.IsApproved);
        }

        return query.OrderBy(t => t.Date).ThenBy(t => t.Id);
    }

    /// <summary>Streams snapshots of <paramref name="query"/> (which must include splits).</summary>
    public async IAsyncEnumerable<TransactionSnapshot> StreamAsync(IQueryable<Transaction> query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var transaction in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            yield return Snapshot(transaction);
        }
    }
}
