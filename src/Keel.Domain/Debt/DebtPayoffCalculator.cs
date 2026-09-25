namespace Keel.Domain.Debt;

/// <summary>Which debt receives the extra payment and every freed minimum first (F-GOAL-2).</summary>
public enum DebtOrdering
{
    /// <summary>Smallest balance first (quick wins).</summary>
    Snowball,

    /// <summary>Highest interest rate first (least interest).</summary>
    Avalanche,
}

/// <summary>A debt as the planner sees it.</summary>
/// <param name="Id">Account id.</param>
/// <param name="Name">Account name (tie-breaker and display).</param>
/// <param name="Balance">Amount owed in minor units, positive (zero or less means already paid off).</param>
/// <param name="AnnualRateBps">Annual interest rate in basis points (1999 = 19.99%).</param>
/// <param name="MinimumPayment">Minimum monthly payment in minor units.</param>
public sealed record DebtInput(Guid Id, string Name, long Balance, int AnnualRateBps, long MinimumPayment);

/// <summary>One debt's month-by-month schedule inside a <see cref="DebtPlan"/>.</summary>
/// <param name="Id">Account id.</param>
/// <param name="Name">Account name.</param>
/// <param name="StartingBalance">Amount owed at the start (0 for a debt already paid off).</param>
/// <param name="AnnualRateBps">Annual rate in basis points.</param>
/// <param name="MinimumPayment">Minimum monthly payment.</param>
/// <param name="Priority">Position in the plan's ordering (0 receives extra money first).</param>
/// <param name="PayoffMonths">
/// The month of the last payment (1 = this month), 0 when nothing is owed, or null when the plan never
/// pays the debt off (the payment does not cover the interest).
/// </param>
/// <param name="TotalInterest">Interest charged until payoff (until the simulation stopped when it never pays off).</param>
/// <param name="TotalPaid">Payments until payoff.</param>
/// <param name="FirstPayment">This month's payment (month 1): the debt-payment target the plan asks for.</param>
/// <param name="Balances">Owed at the start (index 0) and after each month's payment, padded with zeros to the plan length.</param>
public sealed record DebtSchedule(
    Guid Id,
    string Name,
    long StartingBalance,
    int AnnualRateBps,
    long MinimumPayment,
    int Priority,
    int? PayoffMonths,
    long TotalInterest,
    long TotalPaid,
    long FirstPayment,
    IReadOnlyList<long> Balances)
{
    /// <summary>Whether the plan pays this debt off.</summary>
    public bool PaysOff => PayoffMonths is not null;
}

/// <summary>A payoff plan across several debts (F-GOAL-2).</summary>
/// <param name="Ordering">The ordering used.</param>
/// <param name="ExtraPerMonth">Money added to the minimums every month.</param>
/// <param name="MonthlyOutlay">Σ minimums of the debts owed at the start plus the extra: paid every month until the last debt is gone.</param>
/// <param name="Debts">Schedules in priority order.</param>
/// <param name="PayoffMonths">The month the last debt is paid off (0 when nothing is owed), or null when some debt never is.</param>
/// <param name="TotalInterest">Σ interest over every debt.</param>
/// <param name="TotalBalances">Σ owed at the start and after each month.</param>
public sealed record DebtPlan(
    DebtOrdering Ordering,
    long ExtraPerMonth,
    long MonthlyOutlay,
    IReadOnlyList<DebtSchedule> Debts,
    int? PayoffMonths,
    long TotalInterest,
    IReadOnlyList<long> TotalBalances)
{
    /// <summary>Whether every debt is paid off.</summary>
    public bool PaysOffAll => PayoffMonths is not null;
}

/// <summary>
/// Debt payoff math (F-GOAL-2, ADR 0093). Pure and integer-only: each month interest is charged on
/// the balance owed at the start of the month (annual rate / 12, banker's rounding to the minor unit),
/// then every unpaid debt receives its minimum (capped at what it owes) and the rest of the monthly
/// outlay (the extra plus the minimums freed by debts already paid off, "rollover") goes to the debts
/// in priority order: smallest balance first (snowball) or highest rate first (avalanche). A debt whose
/// monthly interest reaches the whole monthly outlay can never shrink (no month pays it more than the
/// outlay, and its interest only grows), so it is reported as never paid off; the plan runs until every
/// other debt is paid off, or for at most <see cref="MaxMonths"/> months. Balances saturate at
/// <see cref="BalanceCeiling"/>.
/// </summary>
public static class DebtPayoffCalculator
{
    /// <summary>Longest plan simulated (100 years).</summary>
    public const int MaxMonths = 1200;

    /// <summary>A balance growing without end stops here (no overflow over a century of interest).</summary>
    public const long BalanceCeiling = long.MaxValue / 1024;

    /// <summary>Largest accepted annual rate in basis points (100%).</summary>
    public const int MaxRateBps = 10_000;

    /// <summary>One month's interest on <paramref name="balance"/>: balance × rate / 12, half to even.</summary>
    public static long MonthlyInterest(long balance, int annualRateBps)
    {
        if (balance <= 0 || annualRateBps <= 0)
        {
            return 0;
        }

        return DivideHalfEven((Int128)balance * annualRateBps, 12 * 10_000);
    }

    /// <summary>The debts in <paramref name="ordering"/> order (ties: the other criterion, then name, then id).</summary>
    public static IReadOnlyList<DebtInput> Order(IEnumerable<DebtInput> debts, DebtOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(debts);
        var list = debts.ToList();
        var ordered = ordering switch
        {
            DebtOrdering.Snowball => list.OrderBy(d => d.Balance).ThenByDescending(d => d.AnnualRateBps),
            DebtOrdering.Avalanche => list.OrderByDescending(d => d.AnnualRateBps).ThenBy(d => d.Balance),
            _ => throw new ArgumentOutOfRangeException(nameof(ordering), ordering, null),
        };
        return [.. ordered.ThenBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id)];
    }

    /// <summary>One debt on its own at its minimum payment ("at current payment").</summary>
    public static DebtSchedule AtMinimum(DebtInput debt)
    {
        ArgumentNullException.ThrowIfNull(debt);
        return Plan([debt], 0, DebtOrdering.Avalanche).Debts[0];
    }

    /// <summary>
    /// Every debt on its own at its minimum, with no rollover: the baseline a plan is compared with.
    /// Schedules are in the given order and padded to the longest one.
    /// </summary>
    public static DebtPlan MinimumOnly(IReadOnlyList<DebtInput> debts)
    {
        ArgumentNullException.ThrowIfNull(debts);
        var singles = debts.Select(AtMinimum).ToList();
        var length = singles.Count == 0 ? 1 : singles.Max(s => s.Balances.Count);
        var padded = singles.Select((s, i) => s with { Priority = i, Balances = Pad(s.Balances, length) }).ToList();
        var never = padded.Any(s => !s.PaysOff);
        return new DebtPlan(
            DebtOrdering.Avalanche,
            0,
            debts.Where(d => d.Balance > 0).Sum(d => Math.Max(0, d.MinimumPayment)),
            padded,
            never ? null : padded.Select(s => s.PayoffMonths!.Value).DefaultIfEmpty(0).Max(),
            padded.Sum(s => s.TotalInterest),
            Enumerable.Range(0, length).Select(m => padded.Sum(s => s.Balances[m])).ToList());
    }

    /// <summary>Plans the payoff of <paramref name="debts"/> with <paramref name="extraPerMonth"/> on top of the minimums.</summary>
    public static DebtPlan Plan(IReadOnlyList<DebtInput> debts, long extraPerMonth, DebtOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(debts);
        ArgumentOutOfRangeException.ThrowIfNegative(extraPerMonth);
        foreach (var debt in debts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(debt.MinimumPayment, nameof(debts));
            if (debt.AnnualRateBps is < 0 or > MaxRateBps)
            {
                throw new ArgumentOutOfRangeException(nameof(debts), debt.AnnualRateBps, "The annual rate must be between 0 and 100%.");
            }
        }

        var order = Order(debts, ordering);
        var n = order.Count;
        var balance = order.Select(d => Math.Max(0, d.Balance)).ToArray();
        var interest = new long[n];
        var paid = new long[n];
        var first = new long[n];
        var payoff = new int?[n];
        var history = Enumerable.Range(0, n).Select(i => new List<long> { balance[i] }).ToArray();
        for (var i = 0; i < n; i++)
        {
            if (balance[i] == 0)
            {
                payoff[i] = 0;
            }
        }

        var outlay = checked(order.Where(d => d.Balance > 0).Sum(d => d.MinimumPayment) + extraPerMonth);
        var doomed = new bool[n];
        var totals = new List<long> { Total(balance) };
        for (var month = 1; month <= MaxMonths; month++)
        {
            // Never paid off: even the whole outlay would not cover this debt's interest.
            for (var i = 0; i < n; i++)
            {
                doomed[i] |= balance[i] > 0 && MonthlyInterest(balance[i], order[i].AnnualRateBps) >= outlay;
            }

            if (Enumerable.Range(0, n).All(i => balance[i] == 0 || doomed[i]))
            {
                break;
            }

            for (var i = 0; i < n; i++)
            {
                var charge = MonthlyInterest(balance[i], order[i].AnnualRateBps);
                balance[i] = Math.Min(BalanceCeiling, balance[i] + charge);
                interest[i] = Math.Min(BalanceCeiling, interest[i] + charge);
            }

            var available = outlay;
            void Pay(int i, long amount)
            {
                var payment = Math.Min(Math.Min(amount, balance[i]), available);
                if (payment <= 0)
                {
                    return;
                }

                balance[i] -= payment;
                available -= payment;
                paid[i] += payment;
                if (month == 1)
                {
                    first[i] += payment;
                }
            }

            for (var i = 0; i < n; i++)
            {
                Pay(i, order[i].MinimumPayment);
            }

            for (var i = 0; i < n && available > 0; i++)
            {
                Pay(i, available);
            }

            for (var i = 0; i < n; i++)
            {
                if (balance[i] == 0 && payoff[i] is null)
                {
                    payoff[i] = month;
                }

                history[i].Add(balance[i]);
            }

            totals.Add(Total(balance));
        }

        var length = totals.Count;
        var schedules = order.Select((d, i) => new DebtSchedule(
            d.Id,
            d.Name,
            Math.Max(0, d.Balance),
            d.AnnualRateBps,
            d.MinimumPayment,
            i,
            payoff[i],
            interest[i],
            paid[i],
            first[i],
            Pad(history[i], length))).ToList();
        var allPaid = balance.All(b => b == 0);
        return new DebtPlan(
            ordering,
            extraPerMonth,
            outlay,
            schedules,
            allPaid ? schedules.Select(s => s.PayoffMonths!.Value).DefaultIfEmpty(0).Max() : null,
            interest.Sum(),
            totals);
    }

    private static long Total(long[] balances)
    {
        Int128 total = 0;
        foreach (var balance in balances)
        {
            total += balance;
        }

        return (long)Int128.Min(total, long.MaxValue);
    }

    private static List<long> Pad(IReadOnlyList<long> values, int length)
    {
        var list = new List<long>(length);
        list.AddRange(values);
        while (list.Count < length)
        {
            list.Add(0);
        }

        return list;
    }

    private static long DivideHalfEven(Int128 numerator, long denominator)
    {
        var quotient = Int128.DivRem(numerator, denominator);
        var q = quotient.Quotient;
        var twice = Int128.Abs(quotient.Remainder) * 2;
        if (twice > denominator || (twice == denominator && q % 2 != 0))
        {
            q += Int128.Sign(numerator);
        }

        return (long)q;
    }
}
