using CsCheck;
using Keel.Domain.Scheduling;

namespace Keel.Domain.Tests.Scheduling;

/// <summary>
/// Properties of <see cref="RecurrenceRule.Occurrences"/> over generated rules and windows:
/// strictly increasing, inside every bound, independent of the window, consistent with
/// <see cref="RecurrenceRule.NextAfter"/>, and the canonical text round-trips.
/// </summary>
public class RecurrenceRulePropertyTests
{
    private static readonly DateOnly Base = new(2020, 1, 1);

    private static readonly Gen<DayOfWeek> GenDay = Gen.Int[0, 6].Select(i => (DayOfWeek)i);

    private static readonly Gen<RecurrenceRule> GenRule =
        Gen.Select(
            Gen.Int[0, 3].Select(i => (RecurrenceFrequency)i),
            Gen.Int[1, 4],
            GenDay.List[0, 3],
            Gen.Int[-5, 5].List[1, 2],
            Gen.OneOf(Gen.Int[1, 31], Gen.Int[-31, -1]).List[0, 3],
            Gen.Int[1, 12].List[0, 2],
            Gen.Int[0, 6],
            Gen.Int[0, 800])
        .Select((freq, interval, days, ordinals, monthDays, months, limitKind, limitValue) =>
        {
            var byDay = freq switch
            {
                RecurrenceFrequency.Daily or RecurrenceFrequency.Weekly => days.Select(d => new WeekdayOccurrence(d)).ToList(),
                _ when monthDays.Count > 0 => [],
                _ => days.Zip(ordinals, (d, o) => new WeekdayOccurrence(d, o)).ToList(),
            };
            var byMonthDay = freq is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly ? monthDays : [];
            var byMonth = freq == RecurrenceFrequency.Yearly && (byDay.Count > 0 || byMonthDay.Count > 0 || months.Count > 0)
                ? (months.Count > 0 ? months : [3])
                : [];
            int? count = limitKind == 1 ? 1 + (limitValue % 40) : null;
            DateOnly? until = limitKind == 2 ? Base.AddDays(limitValue * 3) : null;
            var weekStart = limitKind == 3 ? DayOfWeek.Sunday : DayOfWeek.Monday;
            return RecurrenceRule.Create(freq, interval, byDay, byMonthDay, byMonth, count, until, weekStart);
        });

    private static readonly Gen<(RecurrenceRule Rule, DateOnly Start, DateOnly From, DateOnly To)> GenCase =
        Gen.Select(GenRule, Gen.Int[0, 1500], Gen.Int[-100, 1500], Gen.Int[0, 1200])
        .Select((rule, s, f, len) => (rule, Base.AddDays(s), Base.AddDays(s + f), Base.AddDays(s + f + len)));

    [Fact]
    public void Occurrences_are_strictly_increasing_and_within_bounds()
    {
        GenCase.Sample(c =>
        {
            var dates = c.Rule.Occurrences(c.Start, c.From, c.To).ToList();
            for (var i = 1; i < dates.Count; i++)
            {
                dates[i].ShouldBeGreaterThan(dates[i - 1]);
            }

            foreach (var d in dates)
            {
                d.ShouldBeGreaterThanOrEqualTo(c.Start);
                d.ShouldBeGreaterThanOrEqualTo(c.From);
                d.ShouldBeLessThanOrEqualTo(c.To);
                if (c.Rule.Until is { } until)
                {
                    d.ShouldBeLessThanOrEqualTo(until);
                }
            }

            if (c.Rule.Count is { } count)
            {
                dates.Count.ShouldBeLessThanOrEqualTo(count);
            }
        }, iter: 400);
    }

    [Fact]
    public void A_window_is_a_slice_of_the_full_sequence()
    {
        GenCase.Sample(c =>
        {
            var full = c.Rule.Occurrences(c.Start, c.Start, c.To).ToList();
            var window = c.Rule.Occurrences(c.Start, c.From, c.To).ToList();
            window.ShouldBe(full.Where(d => d >= c.From).ToList());
        }, iter: 400);
    }

    [Fact]
    public void NextAfter_walks_the_sequence()
    {
        GenCase.Sample(c =>
        {
            var full = c.Rule.Occurrences(c.Start, c.Start, c.To).Take(30).ToList();
            for (var i = 0; i + 1 < full.Count; i++)
            {
                c.Rule.NextAfter(c.Start, full[i]).ShouldBe(full[i + 1]);
            }

            if (full.Count > 0)
            {
                c.Rule.First(c.Start).ShouldBe(full[0]);
                c.Rule.NextAfter(c.Start, c.Start.AddDays(-1)).ShouldBe(full[0]);
            }
        }, iter: 300);
    }

    [Fact]
    public void Occurrences_match_the_rule_filters()
    {
        GenCase.Sample(c =>
        {
            foreach (var d in c.Rule.Occurrences(c.Start, c.From, c.To))
            {
                if (c.Rule.ByMonth.Count > 0)
                {
                    c.Rule.ByMonth.ShouldContain(d.Month);
                }

                if (c.Rule.ByDay.Count > 0)
                {
                    c.Rule.ByDay.Select(b => b.Day).ShouldContain(d.DayOfWeek);
                }

                if (c.Rule.Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly && c.Rule.ByMonthDay.Count > 0)
                {
                    var dim = DateTime.DaysInMonth(d.Year, d.Month);
                    c.Rule.ByMonthDay
                        .Select(md => md > 0 ? Math.Min(md, dim) : Math.Max(1, dim + md + 1))
                        .ShouldContain(d.Day);
                }
            }
        }, iter: 300);
    }

    [Fact]
    public void Canonical_text_round_trips()
    {
        GenRule.Sample(rule =>
        {
            var parsed = RecurrenceRule.Parse(rule.ToString());
            parsed.ShouldBe(rule);
            parsed.ToString().ShouldBe(rule.ToString());
            rule.Describe().ShouldNotBeNullOrWhiteSpace();
        }, iter: 400);
    }
}
