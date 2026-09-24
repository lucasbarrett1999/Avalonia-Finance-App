using Keel.Domain;
using Keel.Domain.Import;
using Keel.Domain.Recurring;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Recurring;

/// <summary>A ledger row as detection and alerts see it, with its payee id and category.</summary>
/// <param name="Transaction">The detector's view (normalized payee).</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="CategoryId">Category (null for split parents and uncategorized rows).</param>
internal sealed record RecurringLedgerRow(RecurringTransaction Transaction, Guid PayeeId, Guid? CategoryId);

/// <summary>
/// Loads detection and alert inputs. Rows are non-deleted transactions with a payee that are not
/// transfers and not system rows (starting balances, adjustments); the payee is normalized from the
/// payee's name with <see cref="PayeeNormalizer"/>, the same normalization used for stored items.
/// </summary>
internal static class RecurringInputs
{
    /// <summary>Rows dated <paramref name="from"/> … <paramref name="to"/> plus the rows in <paramref name="extraIds"/>.</summary>
    /// <remarks>With <paramref name="payeeIds"/>, only rows of those payees.</remarks>
    public static async Task<List<RecurringLedgerRow>> LoadAsync(
        KeelDbContext db,
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<Guid>? extraIds,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? payeeIds = null)
    {
        var extra = extraIds?.ToList() ?? [];
        var transactions = db.Transactions.AsNoTracking()
            .Where(t => t.TransferAccountId == null && t.Source != TransactionSource.System && ((t.Date >= from && t.Date <= to) || extra.Contains(t.Id)));
        if (payeeIds is not null)
        {
            var ids = payeeIds.ToList();
            transactions = transactions.Where(t => t.PayeeId != null && ids.Contains(t.PayeeId.Value));
        }

        var query =
            from t in transactions
            join p in db.Payees.AsNoTracking() on t.PayeeId equals p.Id
            select new { t.Id, t.AccountId, t.Date, t.Amount, PayeeId = p.Id, p.Name, t.CategoryId };
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        return rows.Select(r => new RecurringLedgerRow(
            new RecurringTransaction(r.Id, r.AccountId, r.Date, r.Amount, Normalize(names, r.Name)),
            r.PayeeId,
            r.CategoryId)).ToList();
    }

    /// <summary>Normalized payee names by payee id, for stored items.</summary>
    public static async Task<Dictionary<Guid, string>> NormalizedPayeesAsync(KeelDbContext db, IEnumerable<Guid> payeeIds, CancellationToken ct)
    {
        var names = await M5Lookups.PayeeNamesAsync(db, payeeIds, ct).ConfigureAwait(false);
        return names.ToDictionary(kv => kv.Key, kv => PayeeNormalizer.Normalize(kv.Value));
    }

    /// <summary>Ids of the payees whose normalized name is in <paramref name="normalizedNames"/>.</summary>
    public static async Task<List<Guid>> PayeesNamedAsync(KeelDbContext db, IReadOnlySet<string> normalizedNames, CancellationToken ct)
    {
        var payees = await db.Payees.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync(ct).ConfigureAwait(false);
        return payees.Where(p => normalizedNames.Contains(PayeeNormalizer.Normalize(p.Name))).Select(p => p.Id).ToList();
    }

    /// <summary>The look-back window start of detection as of <paramref name="asOf"/>.</summary>
    public static DateOnly WindowStart(DateOnly asOf) => asOf.AddMonths(-RecurringDetector.LookbackMonths);

    private static string Normalize(Dictionary<string, string> cache, string name)
    {
        if (!cache.TryGetValue(name, out var normalized))
        {
            normalized = PayeeNormalizer.Normalize(name);
            cache[name] = normalized;
        }

        return normalized;
    }
}
