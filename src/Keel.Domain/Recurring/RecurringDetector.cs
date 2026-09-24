using Keel.Domain.Budgeting;

namespace Keel.Domain.Recurring;

/// <summary>
/// Recurring detection, PRD 6.6 (normative). Pure: transactions in, detections out; merging with
/// stored items is <see cref="RecurringReconciler"/>. Interpretations are recorded in
/// <c>docs/decisions/0031-recurring-detection-interpretations.md</c>.
/// </summary>
/// <remarks>
/// For each (normalized payee, account) group:
/// <list type="number">
/// <item>Keep the transactions of the last 15 months up to the as-of date, drop $0 rows and keep the
/// group's dominant direction (outflows unless inflows are the majority), so a refund does not break
/// a subscription's gaps.</item>
/// <item>Require ≥ 3 such transactions (2 are enough to be judged for the yearly cadence only).</item>
/// <item>Sort by date, compute day gaps, and score every candidate cadence by the fraction of gaps in
/// its window (<see cref="CadenceWindow"/>). The highest fraction wins (see <see cref="PickBest"/> for
/// the overlapping biweekly and semimonthly windows).</item>
/// <item>Require a fraction ≥ 0.7 and the cadence's minimum occurrence count (3, yearly 2).</item>
/// <item>Amount: median of the last 6; tolerance max($2, 10%); variable when any of them is outside it.</item>
/// <item>Confidence = fraction × (1 or 0.8 when variable).</item>
/// <item>Next expected date: see <see cref="RecurringSchedule.NextExpected"/>.</item>
/// </list>
/// </remarks>
public static class RecurringDetector
{
    /// <summary>Look-back window in months (6.6).</summary>
    public const int LookbackMonths = 15;

    /// <summary>Minimum transactions per group (6.6).</summary>
    public const int MinTransactions = 3;

    /// <summary>Minimum fraction of gaps that must fit the cadence (6.6 step 3).</summary>
    public const double MinFraction = 0.7;

    /// <summary>How many recent occurrences set the amount and the calendar anchor (6.6 step 4).</summary>
    public const int RecentOccurrences = 6;

    /// <summary>Absolute amount tolerance floor: $2 in minor units (6.6 step 4).</summary>
    public const long MinAmountTolerance = 200;

    /// <summary>Confidence factor for variable amounts (6.6 step 5).</summary>
    public const double VariableAmountFactor = 0.8;

    private const double TieEpsilon = 1e-9;

    /// <summary>
    /// Detects recurring patterns in <paramref name="transactions"/> as of <paramref name="asOf"/>.
    /// Transactions after <paramref name="asOf"/> or with an empty payee are ignored. The result is
    /// ordered by payee, then account.
    /// </summary>
    public static IReadOnlyList<DetectedRecurringItem> Detect(IEnumerable<RecurringTransaction> transactions, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var windowStart = asOf.AddMonths(-LookbackMonths);
        var groups = new Dictionary<RecurringGroupKey, List<RecurringTransaction>>();
        foreach (var t in transactions)
        {
            if (t.Date < windowStart || t.Date > asOf || t.Amount == 0 || string.IsNullOrEmpty(t.NormalizedPayee))
            {
                continue;
            }

            var key = t.Key;
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(t);
        }

        var result = new List<DetectedRecurringItem>();
        foreach (var (key, list) in groups)
        {
            if (list.Count < 2)
            {
                continue;
            }

            if (DetectGroup(key, list, asOf) is { } item)
            {
                result.Add(item);
            }
        }

        result.Sort((a, b) => a.Key.CompareTo(b.Key));
        return result;
    }

    /// <summary>
    /// Runs detection for one group. <paramref name="transactions"/> must already be restricted to the
    /// look-back window and to non-zero amounts (as <see cref="Detect"/> does); order does not matter.
    /// </summary>
    public static DetectedRecurringItem? DetectGroup(RecurringGroupKey key, IReadOnlyList<RecurringTransaction> transactions, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var outflows = 0;
        foreach (var t in transactions)
        {
            if (t.Amount < 0)
            {
                outflows++;
            }
        }

        var wantOutflow = outflows * 2 >= transactions.Count;
        var occurrences = new List<RecurringTransaction>(transactions.Count);
        foreach (var t in transactions)
        {
            if (t.Amount != 0 && (t.Amount < 0) == wantOutflow)
            {
                occurrences.Add(t);
            }
        }

        var n = occurrences.Count;
        if (n < 2)
        {
            return null;
        }

        occurrences.Sort(static (a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Id.CompareTo(b.Id));
        var dates = new DateOnly[n];
        for (var i = 0; i < n; i++)
        {
            dates[i] = occurrences[i].Date;
        }

        if (!AnyCadenceCanPass(dates))
        {
            return null; // cheap exit for irregular groups: no window holds 70% of the gaps
        }

        var scores = ScoreCadences(dates);
        var eligible = n >= MinTransactions ? scores : scores.Where(s => s.Cadence == RecurrenceCadence.Yearly).ToList();
        var best = PickBest(eligible);
        if (best is null || best.Fraction < MinFraction - TieEpsilon || n < CadenceWindow.For(best.Cadence).MinOccurrences)
        {
            return null;
        }

        var recent = occurrences.Skip(Math.Max(0, n - RecentOccurrences)).ToList();
        var recentDates = recent.Select(t => t.Date).ToList();
        var amounts = recent.Select(t => t.Amount).ToList();
        var median = Median(amounts);
        var tolerance = Tolerance(median);
        var isVariable = amounts.Any(a => Math.Abs(a - median) > tolerance);
        var confidence = best.Fraction * (isVariable ? VariableAmountFactor : 1.0);

        var next = RecurringSchedule.NextExpected(best.Cadence, recentDates);
        var rule = RecurringSchedule.RuleFor(best.Cadence, recentDates, next);
        var window = CadenceWindow.For(best.Cadence);
        var isLapsed = asOf.DayNumber > next.DayNumber + window.PeriodDays + window.ToleranceDays;

        return new DetectedRecurringItem(
            key,
            best.Cadence,
            median,
            tolerance,
            isVariable,
            next,
            dates[^1],
            dates[0],
            confidence,
            best.Fraction,
            n,
            rule,
            isLapsed,
            occurrences.Select(t => t.Id).ToList(),
            scores);
    }

    private static bool AnyCadenceCanPass(DateOnly[] dates)
    {
        Span<int> fits = stackalloc int[CadenceWindow.All.Count];
        for (var i = 1; i < dates.Length; i++)
        {
            var gap = dates[i].DayNumber - dates[i - 1].DayNumber;
            for (var c = 0; c < fits.Length; c++)
            {
                if (CadenceWindow.All[c].Fits(gap))
                {
                    fits[c]++;
                }
            }
        }

        var gaps = dates.Length - 1;
        foreach (var f in fits)
        {
            if (f >= (MinFraction * gaps) - TieEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Scores every candidate cadence (PRD 6.6 step 2) for dates sorted ascending.</summary>
    public static IReadOnlyList<CadenceScore> ScoreCadences(IReadOnlyList<DateOnly> sortedDates)
    {
        ArgumentNullException.ThrowIfNull(sortedDates);
        var gaps = new int[Math.Max(0, sortedDates.Count - 1)];
        for (var i = 1; i < sortedDates.Count; i++)
        {
            gaps[i - 1] = sortedDates[i].DayNumber - sortedDates[i - 1].DayNumber;
        }

        var scores = new List<CadenceScore>(CadenceWindow.All.Count);
        foreach (var window in CadenceWindow.All)
        {
            var fits = 0;
            foreach (var gap in gaps)
            {
                if (window.Fits(gap))
                {
                    fits++;
                }
            }

            var fraction = gaps.Length == 0 ? 0 : (double)fits / gaps.Length;
            scores.Add(new CadenceScore(window.Cadence, fraction, MeanResidual(sortedDates, window.NominalDays)));
        }

        return scores;
    }

    /// <summary>Median; for an even count, the mean of the middle two rounded half to even.</summary>
    public static long Median(IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : QuickAssign.DivideHalfEven(sorted[mid - 1] + sorted[mid], 2);
    }

    /// <summary>Amount tolerance: max($2, 10% of |amount|), 10% rounded half to even (6.6 step 4).</summary>
    public static long Tolerance(long amount) =>
        Math.Max(MinAmountTolerance, QuickAssign.DivideHalfEven(Math.Abs(amount), 10));

    /// <summary>
    /// PRD 6.6 step 3: the highest fraction wins. Where two cadences' gap windows overlap (biweekly
    /// 11–17 and semimonthly 13–18 days) and both clear <see cref="MinFraction"/>, the fraction cannot
    /// tell them apart, so the smaller mean residual wins (ADR 0031). Remaining ties go to the
    /// shorter cadence.
    /// </summary>
    public static CadenceScore? PickBest(IEnumerable<CadenceScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var list = scores.ToList();
        CadenceScore? best = null;
        foreach (var s in list)
        {
            if (best is null
                || s.Fraction > best.Fraction + TieEpsilon
                || (Math.Abs(s.Fraction - best.Fraction) <= TieEpsilon && s.MeanResidualDays < best.MeanResidualDays - TieEpsilon))
            {
                best = s;
            }
        }

        if (best is null || best.Fraction < MinFraction - TieEpsilon)
        {
            return best;
        }

        var winner = CadenceWindow.For(best.Cadence);
        foreach (var s in list)
        {
            var window = CadenceWindow.For(s.Cadence);
            if (s.Cadence != best.Cadence
                && window.MinGap <= winner.MaxGap
                && winner.MinGap <= window.MaxGap
                && s.Fraction >= MinFraction - TieEpsilon
                && s.MeanResidualDays < best.MeanResidualDays - TieEpsilon)
            {
                best = s;
            }
        }

        return best;
    }

    // Mean |t_k - (a + k * period)| with the phase a fitted as the mean offset: small for the true period,
    // growing with k for a wrong one (the dates drift away from the schedule).
    private static double MeanResidual(IReadOnlyList<DateOnly> dates, double period)
    {
        if (dates.Count == 0)
        {
            return 0;
        }

        var origin = dates[0].DayNumber;
        double sum = 0;
        for (var k = 0; k < dates.Count; k++)
        {
            sum += dates[k].DayNumber - origin - (k * period);
        }

        var phase = sum / dates.Count;
        double residual = 0;
        for (var k = 0; k < dates.Count; k++)
        {
            residual += Math.Abs(dates[k].DayNumber - origin - (k * period) - phase);
        }

        return residual / dates.Count;
    }
}
