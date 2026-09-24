namespace Keel.Domain.Reports;

/// <summary>A dated net amount of ledger activity (one day or one month) in minor units.</summary>
/// <param name="Date">Date of the activity; every row of the amount is on or before it and after the previous entry.</param>
/// <param name="Amount">Net amount.</param>
public readonly record struct LedgerChange(DateOnly Date, long Amount);

/// <summary>A reported balance on a date (F-ACC-7) in minor units.</summary>
public readonly record struct ReportedBalance(DateOnly Date, long Balance);

/// <summary>
/// Account balances at report points (F-REP-3, F-ACC-7). The balance at a point is the ledger
/// balance (the sum of all activity on or before it); for an account that uses snapshots, the
/// latest snapshot on or before the point replaces the ledger up to its date and activity dated
/// after the snapshot is added on top (ADR 0060).
/// </summary>
public static class BalanceSeries
{
    /// <summary>Balances at each point.</summary>
    /// <param name="points">Points in ascending order.</param>
    /// <param name="changes">Ledger activity; any order.</param>
    /// <param name="snapshots">Reported balances; empty for accounts that use the ledger only.</param>
    public static long[] At(IReadOnlyList<DateOnly> points, IEnumerable<LedgerChange> changes, IEnumerable<ReportedBalance> snapshots)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(snapshots);

        var ordered = changes.OrderBy(c => c.Date).ToArray();
        var prefix = new long[ordered.Length + 1];
        for (var i = 0; i < ordered.Length; i++)
        {
            prefix[i + 1] = checked(prefix[i] + ordered[i].Amount);
        }

        var dates = ordered.Select(c => c.Date).ToArray();
        var reported = snapshots.OrderBy(s => s.Date).ToArray();
        var result = new long[points.Count];
        for (var p = 0; p < points.Count; p++)
        {
            var point = points[p];
            var ledgerToPoint = prefix[CountOnOrBefore(dates, point)];
            var snapshot = LatestOnOrBefore(reported, point);
            result[p] = snapshot is { } s
                ? checked(s.Balance + ledgerToPoint - prefix[CountOnOrBefore(dates, s.Date)])
                : ledgerToPoint;
        }

        return result;
    }

    private static int CountOnOrBefore(DateOnly[] dates, DateOnly date)
    {
        // Upper bound: index of the first date after `date`.
        int lo = 0, hi = dates.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (dates[mid] <= date)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static ReportedBalance? LatestOnOrBefore(ReportedBalance[] snapshots, DateOnly date)
    {
        ReportedBalance? found = null;
        foreach (var s in snapshots)
        {
            if (s.Date > date)
            {
                break;
            }

            found = s;
        }

        return found;
    }
}
