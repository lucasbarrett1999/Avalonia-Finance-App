namespace Keel.Domain.Ledger;

/// <summary>
/// Ledger order and running balances. The ledger order of an account is (Date, Id): by date, then
/// by the time-ordered id, i.e. entry order within a day. A row's running balance is the sum of
/// every non-deleted transaction at or before it in ledger order, whatever order the register
/// shows (see docs/decisions/0005-register-running-balance.md). The register computes it in SQL;
/// this reference implementation is used by tests.
/// </summary>
public static class RunningBalance
{
    /// <summary>Compares two rows in ledger order.</summary>
    public static int CompareLedgerOrder(DateOnly dateA, Guid idA, DateOnly dateB, Guid idB)
    {
        var byDate = dateA.CompareTo(dateB);
        return byDate != 0 ? byDate : string.CompareOrdinal(SortKey(idA), SortKey(idB));
    }

    /// <summary>The text form used to order ids (upper-case "D" format, as stored by SQLite).</summary>
    public static string SortKey(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>Running balance per row id, starting from <paramref name="openingBalance"/>.</summary>
    public static IReadOnlyDictionary<Guid, long> Compute(IEnumerable<(Guid Id, DateOnly Date, long Amount)> rows, long openingBalance = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ordered = rows.ToList();
        ordered.Sort((a, b) => CompareLedgerOrder(a.Date, a.Id, b.Date, b.Id));
        var result = new Dictionary<Guid, long>(ordered.Count);
        var balance = openingBalance;
        foreach (var row in ordered)
        {
            balance = checked(balance + row.Amount);
            result[row.Id] = balance;
        }

        return result;
    }
}
