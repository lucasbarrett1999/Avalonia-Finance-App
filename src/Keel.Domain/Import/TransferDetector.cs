namespace Keel.Domain.Import;

/// <summary>A transaction that may be one side of a transfer between the user's own accounts.</summary>
/// <param name="Id">Transaction id (incoming rows get their id before insert, see <c>EntityIds.New</c>).</param>
/// <param name="AccountId">Account of the transaction.</param>
/// <param name="Date">Ledger date.</param>
/// <param name="Amount">Signed amount in minor units.</param>
/// <param name="IsIncoming">True for rows of the batch being imported; false for existing rows.</param>
public sealed record TransferCandidate(Guid Id, Guid AccountId, DateOnly Date, long Amount, bool IsIncoming);

/// <summary>A proposed transfer pair.</summary>
/// <param name="OutflowId">The negative side.</param>
/// <param name="InflowId">The positive side.</param>
/// <param name="DayDifference">Absolute date distance in days.</param>
/// <param name="IsAmbiguous">True when either side had another equally close partner, so the
/// preview should ask rather than assume.</param>
public sealed record TransferMatch(Guid OutflowId, Guid InflowId, int DayDifference, bool IsAmbiguous);

/// <summary>
/// Transfer detection for the import pipeline (F-TXN-1 step 5): pairs opposite amounts in
/// different accounts within ±3 days. At least one side of every pair must be an incoming row;
/// existing rows passed in must not already be part of a transfer.
/// </summary>
public static class TransferDetector
{
    /// <summary>The PRD window: ±3 days.</summary>
    public const int DefaultMaxDays = 3;

    /// <summary>
    /// Finds transfer pairs. Each transaction is used at most once. Closest dates win; ties are
    /// broken by outflow date, then outflow id, then inflow id, so the result is deterministic.
    /// </summary>
    public static IReadOnlyList<TransferMatch> Detect(IEnumerable<TransferCandidate> candidates, int maxDays = DefaultMaxDays)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDays);

        var list = candidates.Where(c => c.Amount != 0).ToList();
        var inflowsByAmount = list.Where(c => c.Amount > 0)
            .GroupBy(c => c.Amount)
            .ToDictionary(g => g.Key, g => g.ToList());

        var pairs = new List<(TransferCandidate Out, TransferCandidate In, int Days)>();
        foreach (var outflow in list.Where(c => c.Amount < 0))
        {
            if (outflow.Amount == long.MinValue || !inflowsByAmount.TryGetValue(-outflow.Amount, out var inflows))
            {
                continue;
            }

            foreach (var inflow in inflows)
            {
                var days = Math.Abs(outflow.Date.DayNumber - inflow.Date.DayNumber);
                if (inflow.AccountId != outflow.AccountId && days <= maxDays && (outflow.IsIncoming || inflow.IsIncoming))
                {
                    pairs.Add((outflow, inflow, days));
                }
            }
        }

        var partnerCount = new Dictionary<(Guid Id, int Days), int>();
        foreach (var (o, i, d) in pairs)
        {
            partnerCount[(o.Id, d)] = partnerCount.GetValueOrDefault((o.Id, d)) + 1;
            partnerCount[(i.Id, d)] = partnerCount.GetValueOrDefault((i.Id, d)) + 1;
        }

        var used = new HashSet<Guid>();
        var matches = new List<TransferMatch>();
        foreach (var (o, i, d) in pairs
            .OrderBy(p => p.Days)
            .ThenBy(p => p.Out.Date)
            .ThenBy(p => p.Out.Id)
            .ThenBy(p => p.In.Id))
        {
            if (used.Contains(o.Id) || used.Contains(i.Id))
            {
                continue;
            }

            used.Add(o.Id);
            used.Add(i.Id);
            var ambiguous = partnerCount[(o.Id, d)] > 1 || partnerCount[(i.Id, d)] > 1;
            matches.Add(new TransferMatch(o.Id, i.Id, d, ambiguous));
        }

        return matches;
    }
}
