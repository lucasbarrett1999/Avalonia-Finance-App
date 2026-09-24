using Keel.Domain.Recurring;

namespace Keel.Domain.Tests.Recurring;

/// <summary>
/// Deterministic synthetic transaction series for recurring detection, alerts and forecasts.
/// Also compiled into Keel.Benchmarks.
/// </summary>
public static class RecurringSeries
{
    /// <summary>A fixed account id for tests (<c>…-000000000001</c> for 1).</summary>
    public static Guid Account(int n) => Guid.Parse($"0190a1b2-0000-7000-8000-{n:D12}");

    /// <summary>A fixed id in a namespace (1 = transactions, 2 = items, …) for tests.</summary>
    public static Guid Id(int space, int n) => Guid.Parse($"0190a1b2-{space:D4}-7000-9000-{n:D12}");

    /// <summary>
    /// Nominal dates of a cadence: every 7 or 14 days, twice monthly on <paramref name="semimonthlyDays"/>,
    /// or every 1, 3 or 12 months on the day of <paramref name="first"/> (clamped to short months).
    /// </summary>
    public static IEnumerable<DateOnly> Dates(RecurrenceCadence cadence, DateOnly first, int count, (int A, int B)? semimonthlyDays = null)
    {
        switch (cadence)
        {
            case RecurrenceCadence.Weekly:
            case RecurrenceCadence.Biweekly:
                {
                    var step = cadence == RecurrenceCadence.Weekly ? 7 : 14;
                    for (var i = 0; i < count; i++)
                    {
                        yield return first.AddDays(i * step);
                    }

                    yield break;
                }

            case RecurrenceCadence.Semimonthly:
                {
                    var (a, b) = semimonthlyDays ?? (1, 15);
                    var month = new DateOnly(first.Year, first.Month, 1);
                    var emitted = 0;
                    while (emitted < count)
                    {
                        foreach (var key in new[] { a, b })
                        {
                            var d = RecurringSchedule.OnDay(month.Year, month.Month, key);
                            if (d >= first && emitted < count)
                            {
                                emitted++;
                                yield return d;
                            }
                        }

                        month = month.AddMonths(1);
                    }

                    yield break;
                }

            default:
                {
                    var months = cadence switch
                    {
                        RecurrenceCadence.Monthly => 1,
                        RecurrenceCadence.Quarterly => 3,
                        _ => 12,
                    };
                    for (var i = 0; i < count; i++)
                    {
                        var m = new DateOnly(first.Year, first.Month, 1).AddMonths(i * months);
                        yield return RecurringSchedule.OnDay(m.Year, m.Month, first.Day);
                    }

                    yield break;
                }
        }
    }

    /// <summary>Transactions on <paramref name="dates"/>, with optional date jitter and relative amount noise.</summary>
    public static List<RecurringTransaction> Build(
        string payee,
        Guid account,
        IEnumerable<DateOnly> dates,
        long amount,
        Random? random = null,
        int jitterDays = 0,
        double amountNoise = 0,
        Func<int, Guid>? ids = null,
        int idOffset = 0)
    {
        var list = new List<RecurringTransaction>();
        var i = 0;
        foreach (var date in dates)
        {
            var d = jitterDays == 0 || random is null ? date : date.AddDays(random.Next(-jitterDays, jitterDays + 1));
            var a = amountNoise == 0 || random is null
                ? amount
                : (long)Math.Round(amount * (1 + (((random.NextDouble() * 2) - 1) * amountNoise)));
            list.Add(new RecurringTransaction((ids ?? (n => Id(1, n)))(idOffset + i), account, d, a, payee));
            i++;
        }

        return list;
    }
}

/// <summary>A labeled group of generated transactions: the cadence detection should find, or null.</summary>
/// <param name="Key">Group.</param>
/// <param name="Expected">Expected cadence; null for an irregular payee that must not be detected.</param>
public sealed record LabeledGroup(RecurringGroupKey Key, RecurrenceCadence? Expected);

/// <summary>
/// Generates a large, labeled, deterministic ledger for detector accuracy and performance: about
/// 35% irregular payees (1–6 day gaps, noisy amounts, many rows) and the rest recurring on a random
/// cadence with jitter inside half the PRD tolerance and at most 4% amount noise.
/// </summary>
public static class RecurringFixtureGenerator
{
    /// <summary>Generates <paramref name="payees"/> groups ending on or before <paramref name="asOf"/>.</summary>
    public static (List<RecurringTransaction> Transactions, List<LabeledGroup> Labels) Generate(int payees, DateOnly asOf, int seed)
    {
        var random = new Random(seed);
        var transactions = new List<RecurringTransaction>(payees * 60);
        var labels = new List<LabeledGroup>(payees);
        var accounts = Enumerable.Range(1, 4).Select(RecurringSeries.Account).ToArray();
        var start = asOf.AddMonths(-RecurringDetector.LookbackMonths).AddDays(1);
        var span = asOf.DayNumber - start.DayNumber;
        var nextId = 0;
        Guid NewId(int _) => RecurringSeries.Id(1, nextId++);

        for (var p = 0; p < payees; p++)
        {
            var payee = $"PAYEE {p:D5}";
            var account = accounts[random.Next(accounts.Length)];
            if (random.NextDouble() < 0.35)
            {
                var dates = new List<DateOnly>();
                for (var d = start.AddDays(random.Next(0, 10)); d <= asOf; d = d.AddDays(random.Next(1, 7)))
                {
                    dates.Add(d);
                }

                transactions.AddRange(RecurringSeries.Build(payee, account, dates, -(500 + random.Next(0, 5000)), random, 0, 0.5, NewId));
                labels.Add(new LabeledGroup(new RecurringGroupKey(payee, account), null));
                continue;
            }

            var cadence = (RecurrenceCadence)random.Next(0, 6);
            var window = CadenceWindow.For(cadence);
            var first = start.AddDays(window.ToleranceDays + random.Next(0, Math.Min(window.PeriodDays, span / 4)));
            var count = Math.Max(window.MinOccurrences, (int)((asOf.DayNumber - first.DayNumber) / window.NominalDays) + 1);
            var nominal = RecurringSeries.Dates(cadence, first, count + 1, random.Next(2) == 0 ? (1, 15) : (15, -1))
                .Where(d => d <= asOf.AddDays(-window.ToleranceDays))
                .ToList();
            var amount = random.Next(4) == 0 ? 100_000 + random.Next(0, 300_000) : -(300 + random.Next(0, 200_000));
            transactions.AddRange(RecurringSeries.Build(payee, account, nominal, amount, random, window.ToleranceDays / 2, 0.04, NewId));
            labels.Add(new LabeledGroup(
                new RecurringGroupKey(payee, account),
                nominal.Count >= window.MinOccurrences ? cadence : null));
        }

        return (transactions, labels);
    }
}
