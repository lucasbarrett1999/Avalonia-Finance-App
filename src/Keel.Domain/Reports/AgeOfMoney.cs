namespace Keel.Domain.Reports;

/// <summary>One day of money in and out of on-budget cash accounts (the age-of-money input).</summary>
/// <param name="Date">Day.</param>
/// <param name="Inflow">Σ positive amounts (minor units, ≥ 0).</param>
/// <param name="Outflow">Σ negative amounts as a positive number (minor units, ≥ 0).</param>
/// <param name="OutflowCount">Number of outflow transactions that day.</param>
public sealed record DailyCashFlow(DateOnly Date, long Inflow, long Outflow, int OutflowCount);

/// <summary>A day of outflows counted in an age of money.</summary>
/// <param name="Date">Day of the outflows.</param>
/// <param name="Count">Outflows of that day inside the window.</param>
/// <param name="AgeDays">Amount-weighted age of the money they spent, in days.</param>
public sealed record AgeOfMoneyOutflow(DateOnly Date, int Count, decimal AgeDays);

/// <summary>Age of money at a date.</summary>
/// <param name="Date">Point.</param>
/// <param name="Days">Average age in whole days (half to even), or null when no funded outflow exists yet.</param>
/// <param name="Window">The outflow days averaged, newest first (at most <see cref="AgeOfMoney.OutflowWindow"/> outflows).</param>
public sealed record AgeOfMoneyPoint(DateOnly Date, int? Days, IReadOnlyList<AgeOfMoneyOutflow> Window)
{
    /// <summary>Outflows averaged.</summary>
    public int OutflowCount => Window.Sum(w => w.Count);
}

/// <summary>
/// Age of money (F-REP-5, YNAB definition; ADR 0094): money received into on-budget cash accounts is
/// spent first in, first out. The age of an outflow is the number of days between the inflows that
/// funded it and the outflow; the age of money is the average age of the last
/// <see cref="OutflowWindow"/> outflows. Inflows and outflows come aggregated per day: a day's
/// inflows are available to its own outflows, and all outflows of a day share the day's
/// amount-weighted age. Outflows beyond every inflow so far (an overdrawn budget) are not funded:
/// they are left out of the average and the next inflows repay that shortfall first.
/// </summary>
public static class AgeOfMoney
{
    /// <summary>How many of the latest outflows are averaged.</summary>
    public const int OutflowWindow = 10;

    /// <summary>Age of money at each of <paramref name="points"/> (after all activity on or before the point).</summary>
    public static IReadOnlyList<AgeOfMoneyPoint> At(IEnumerable<DailyCashFlow> days, IReadOnlyList<DateOnly> points)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(points);
        var ordered = days.OrderBy(d => d.Date).ToList();
        var pointOrder = points.Select((p, i) => (Date: p, Index: i)).OrderBy(p => p.Date).ToList();
        var results = new AgeOfMoneyPoint[points.Count];

        var lots = new Queue<Lot>();
        long deficit = 0;
        var recent = new LinkedList<FundedDay>();
        var recentCount = 0;
        var next = 0;
        foreach (var (point, index) in pointOrder)
        {
            for (; next < ordered.Count && ordered[next].Date <= point; next++)
            {
                var day = ordered[next];
                if (day.Inflow < 0 || day.Outflow < 0 || day.OutflowCount < 0)
                {
                    throw new ArgumentException("Inflows, outflows and counts are never negative.", nameof(days));
                }

                // Inflows repay an earlier shortfall first, then queue up oldest first.
                var inflow = day.Inflow;
                var repaid = Math.Min(deficit, inflow);
                deficit -= repaid;
                inflow -= repaid;
                if (inflow > 0)
                {
                    lots.Enqueue(new Lot(day.Date, inflow));
                }

                var outflow = day.Outflow;
                long funded = 0;
                Int128 weighted = 0;
                while (outflow > 0 && lots.Count > 0)
                {
                    var lot = lots.Peek();
                    var take = Math.Min(outflow, lot.Remaining);
                    weighted += (Int128)take * (day.Date.DayNumber - lot.Date.DayNumber);
                    funded += take;
                    outflow -= take;
                    lot.Remaining -= take;
                    if (lot.Remaining == 0)
                    {
                        lots.Dequeue();
                    }
                }

                deficit += outflow;
                if (funded > 0 && day.OutflowCount > 0)
                {
                    recent.AddLast(new FundedDay(day.Date, day.OutflowCount, (decimal)weighted / funded));
                    recentCount += day.OutflowCount;
                    while (recent.First is { } oldest && recentCount - oldest.Value.Count >= OutflowWindow)
                    {
                        recentCount -= oldest.Value.Count;
                        recent.RemoveFirst();
                    }
                }
            }

            results[index] = Evaluate(point, recent);
        }

        return results;
    }

    private static AgeOfMoneyPoint Evaluate(DateOnly point, LinkedList<FundedDay> recent)
    {
        var window = new List<AgeOfMoneyOutflow>();
        var taken = 0;
        decimal sum = 0;
        for (var node = recent.Last; node is not null && taken < OutflowWindow; node = node.Previous)
        {
            var count = Math.Min(node.Value.Count, OutflowWindow - taken);
            window.Add(new AgeOfMoneyOutflow(node.Value.Date, count, node.Value.Age));
            sum += count * node.Value.Age;
            taken += count;
        }

        return new AgeOfMoneyPoint(point, taken == 0 ? null : (int)Math.Round(sum / taken, MidpointRounding.ToEven), window);
    }

    private sealed class Lot(DateOnly date, long remaining)
    {
        public DateOnly Date { get; } = date;

        public long Remaining { get; set; } = remaining;
    }

    private readonly record struct FundedDay(DateOnly Date, int Count, decimal Age);
}
