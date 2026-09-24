using System.Globalization;
using Keel.Domain.Scheduling;

namespace Keel.Domain.Tests.Scheduling;

public class RecurrenceRuleTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Dates(IEnumerable<DateOnly> dates) =>
        dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();

    // Parsing, canonical form and descriptions -----------------------------------------------------

    [Theory]
    [InlineData("FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15", "FREQ=MONTHLY;BYMONTHDAY=15", "Every month on the 15th")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "Every 2 weeks on Friday")]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU", "FREQ=MONTHLY;BYDAY=2TU", "Every month on the 2nd Tuesday")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=1,15", "FREQ=MONTHLY;BYMONTHDAY=1,15", "Every month on the 1st and 15th")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1,15", "FREQ=MONTHLY;BYMONTHDAY=15,-1", "Every month on the 15th and last day")]
    [InlineData("FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=1", "FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=1", "Every year on March 1")]
    [InlineData("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=-1", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=-1", "Every year on the last day of February")]
    [InlineData("FREQ=YEARLY;BYMONTH=3,9;BYMONTHDAY=1", "FREQ=YEARLY;BYMONTH=3,9;BYMONTHDAY=1", "Every year on the 1st of March and September")]
    [InlineData("FREQ=YEARLY;BYMONTH=11;BYDAY=4TH", "FREQ=YEARLY;BYMONTH=11;BYDAY=4TH", "Every year on the 4th Thursday of November")]
    [InlineData("FREQ=DAILY", "FREQ=DAILY", "Every day")]
    [InlineData("FREQ=DAILY;INTERVAL=3;COUNT=1", "FREQ=DAILY;INTERVAL=3;COUNT=1", "Every 3 days, once")]
    [InlineData("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "Every weekday")]
    [InlineData("FREQ=DAILY;BYDAY=SA,SU", "FREQ=DAILY;BYDAY=SA,SU", "Every day on Saturday and Sunday")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", "FREQ=MONTHLY;BYMONTHDAY=-1", "Every month on the last day")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-2", "FREQ=MONTHLY;BYMONTHDAY=-2", "Every month on the 2nd-to-last day")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "FREQ=MONTHLY;BYDAY=-1FR", "Every month on the last Friday")]
    [InlineData("FREQ=MONTHLY;BYDAY=-2MO", "FREQ=MONTHLY;BYDAY=-2MO", "Every month on the 2nd-to-last Monday")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO", "FREQ=MONTHLY;BYDAY=MO", "Every month on every Monday")]
    [InlineData("FREQ=MONTHLY;BYDAY=3WE,1MO", "FREQ=MONTHLY;BYDAY=1MO,3WE", "Every month on the 1st Monday and the 3rd Wednesday")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=21,22,23,11", "FREQ=MONTHLY;BYMONTHDAY=11,21,22,23", "Every month on the 11th, 21st, 22nd and 23rd")]
    [InlineData("rrule:freq=weekly;byday=th,mo;count=10;", "FREQ=WEEKLY;BYDAY=MO,TH;COUNT=10", "Every week on Monday and Thursday, 10 times")]
    [InlineData("FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=1;UNTIL=20271231T235959Z", "FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=1;UNTIL=20271231", "Every 3 months on the 1st, until 2027-12-31")]
    [InlineData(" FREQ = WEEKLY ; INTERVAL = 2 ; BYDAY = SU ; WKST = SU ", "FREQ=WEEKLY;INTERVAL=2;BYDAY=SU;WKST=SU", "Every 2 weeks on Sunday")]
    [InlineData("FREQ=YEARLY;INTERVAL=2", "FREQ=YEARLY;INTERVAL=2", "Every 2 years")]
    [InlineData("FREQ=WEEKLY", "FREQ=WEEKLY", "Every week")]
    [InlineData("FREQ=MONTHLY", "FREQ=MONTHLY", "Every month")]
    public void Parses_to_canonical_form_and_describes(string text, string canonical, string description)
    {
        var rule = RecurrenceRule.Parse(text);
        rule.ToString().ShouldBe(canonical);
        rule.Describe().ShouldBe(description);
        RecurrenceRule.Parse(canonical).ShouldBe(rule);
    }

    [Theory]
    [InlineData("FREQ=WEEKLY", "2026-09-04", "Every week on Friday")]
    [InlineData("FREQ=MONTHLY", "2026-01-31", "Every month on the 31st")]
    [InlineData("FREQ=YEARLY", "2024-02-29", "Every year on February 29")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15", "2026-01-01", "Every month on the 15th")]
    public void Describes_values_taken_from_the_start_date(string text, string start, string description)
    {
        RecurrenceRule.Parse(text).Describe(D(start)).ShouldBe(description);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("INTERVAL=2", "FREQ is required")]
    [InlineData("FREQ=HOURLY", "FREQ=HOURLY is not supported")]
    [InlineData("FREQ=FORTNIGHTLY", "FREQ 'FORTNIGHTLY' is not valid")]
    [InlineData("FREQ=MONTHLY;INTERVAL=0", "INTERVAL '0' must be a whole number from 1 to 999")]
    [InlineData("FREQ=MONTHLY;INTERVAL=two", "INTERVAL 'two'")]
    [InlineData("FREQ=MONTHLY;INTERVAL=1000", "INTERVAL '1000'")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0", "BYMONTHDAY entry '0'")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=32", "BYMONTHDAY entry '32'")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-32", "BYMONTHDAY entry '-32'")]
    [InlineData("FREQ=MONTHLY;BYDAY=6TU", "BYDAY entry '6TU'")]
    [InlineData("FREQ=MONTHLY;BYDAY=TUESDAY", "BYDAY entry 'TUESDAY'")]
    [InlineData("FREQ=MONTHLY;BYDAY=0TU", "BYDAY entry '0TU'")]
    [InlineData("FREQ=WEEKLY;BYDAY=2TU", "ordinals such as 2TU need FREQ=MONTHLY")]
    [InlineData("FREQ=DAILY;BYDAY=1MO", "FREQ=DAILY takes plain weekdays")]
    [InlineData("FREQ=DAILY;BYMONTHDAY=1", "FREQ=DAILY supports only BYDAY")]
    [InlineData("FREQ=WEEKLY;BYMONTHDAY=1", "FREQ=WEEKLY supports only BYDAY")]
    [InlineData("FREQ=MONTHLY;COUNT=3;UNTIL=20270101", "COUNT and UNTIL cannot be used together")]
    [InlineData("FREQ=MONTHLY;COUNT=0", "COUNT '0'")]
    [InlineData("FREQ=MONTHLY;BYSETPOS=-1", "BYSETPOS is not supported")]
    [InlineData("FREQ=YEARLY;BYWEEKNO=20", "BYWEEKNO is not supported")]
    [InlineData("FREQ=DAILY;BYHOUR=9", "BYHOUR is not supported")]
    [InlineData("FREQ=MONTHLY;FOO=1", "Unknown rule part 'FOO'")]
    [InlineData("FREQ=MONTHLY;FREQ=WEEKLY", "FREQ appears more than once")]
    [InlineData("FREQ=MONTHLY;BYMONTH=3", "BYMONTH is supported only with FREQ=YEARLY")]
    [InlineData("FREQ=MONTHLY;BYMONTH=13", "BYMONTH entry '13'")]
    [InlineData("FREQ=MONTHLY;BYDAY=TU;BYMONTHDAY=1", "BYDAY and BYMONTHDAY cannot be combined")]
    [InlineData("FREQ=YEARLY;BYDAY=1MO", "FREQ=YEARLY with BYDAY needs BYMONTH")]
    [InlineData("FREQ=YEARLY;BYMONTHDAY=1", "FREQ=YEARLY with BYMONTHDAY needs BYMONTH")]
    [InlineData("FREQ=MONTHLY;UNTIL=2027-12-31", "UNTIL '2027-12-31'")]
    [InlineData("FREQ=MONTHLY;UNTIL=20270231", "UNTIL '20270231'")]
    [InlineData("FREQ=MONTHLY;UNTIL=20271231T25", "UNTIL '20271231T25'")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY", "'BYMONTHDAY' is not a NAME=VALUE rule part")]
    [InlineData("FREQ=", "FREQ has no value")]
    [InlineData("FREQ=WEEKLY;WKST=XX", "WKST 'XX'")]
    public void Rejects_invalid_rules_with_a_clear_message(string text, string message)
    {
        RecurrenceRule.TryParse(text, out var rule, out var error).ShouldBeFalse();
        rule.ShouldBeNull();
        error.ShouldContain(message);
        Should.Throw<RecurrenceRuleFormatException>(() => RecurrenceRule.Parse(text)).Message.ShouldBe(error);
    }

    [Fact]
    public void Null_is_rejected()
    {
        RecurrenceRule.TryParse(null, out _, out var error).ShouldBeFalse();
        error.ShouldContain("empty");
        Should.Throw<ArgumentNullException>(() => RecurrenceRule.Parse(null!));
    }

    [Fact]
    public void Factories_build_the_expected_rules()
    {
        RecurrenceRule.Daily().ToString().ShouldBe("FREQ=DAILY");
        RecurrenceRule.Weekly(DayOfWeek.Friday, 2).ToString().ShouldBe("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR");
        RecurrenceRule.MonthlyOnDay(-1).ToString().ShouldBe("FREQ=MONTHLY;BYMONTHDAY=-1");
        RecurrenceRule.TwiceMonthly(15, 1).ToString().ShouldBe("FREQ=MONTHLY;BYMONTHDAY=1,15");
        RecurrenceRule.MonthlyOnWeekday(2, DayOfWeek.Tuesday).ToString().ShouldBe("FREQ=MONTHLY;BYDAY=2TU");
        RecurrenceRule.Yearly(3, 1).ToString().ShouldBe("FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=1");
        RecurrenceRule.Daily().WithCount(5).ToString().ShouldBe("FREQ=DAILY;COUNT=5");
        RecurrenceRule.Daily().WithCount(5).WithUntil(D("2027-01-31")).ToString().ShouldBe("FREQ=DAILY;UNTIL=20270131");
        RecurrenceRule.Daily().WithUntil(D("2027-01-31")).WithCount(2).ToString().ShouldBe("FREQ=DAILY;COUNT=2");
        RecurrenceRule.Daily().WithCount(5).WithCount(null).ToString().ShouldBe("FREQ=DAILY");
        Should.Throw<ArgumentException>(() => RecurrenceRule.MonthlyOnDay(0));
        Should.Throw<ArgumentException>(() => RecurrenceRule.Create(RecurrenceFrequency.Weekly, interval: 0));
    }

    [Fact]
    public void Equality_is_by_canonical_form()
    {
        var a = RecurrenceRule.Parse("FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15,1");
        var b = RecurrenceRule.TwiceMonthly(1, 15);
        (a == b).ShouldBeTrue();
        a.Equals((object)b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        (a != RecurrenceRule.MonthlyOnDay(1)).ShouldBeTrue();
        a.Equals(null).ShouldBeFalse();
    }

    // Occurrences --------------------------------------------------------------------------------

    public static TheoryData<string, string, string, string, string[]> OccurrenceTable => new()
    {
        // Short months: the 31st is the last day of shorter months, and the anchor is kept.
        { "FREQ=MONTHLY;BYMONTHDAY=31", "2026-01-01", "2026-01-01", "2026-06-30", ["2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30", "2026-05-31", "2026-06-30"] },
        { "FREQ=MONTHLY", "2026-01-31", "2026-01-01", "2026-05-31", ["2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30", "2026-05-31"] },
        { "FREQ=MONTHLY", "2028-01-30", "2028-01-01", "2028-04-30", ["2028-01-30", "2028-02-29", "2028-03-30", "2028-04-30"] },
        { "FREQ=MONTHLY;BYMONTHDAY=30,31", "2026-01-01", "2026-01-01", "2026-03-31", ["2026-01-30", "2026-01-31", "2026-02-28", "2026-03-30", "2026-03-31"] },
        { "FREQ=MONTHLY;BYMONTHDAY=-1", "2027-12-15", "2027-12-01", "2028-04-30", ["2027-12-31", "2028-01-31", "2028-02-29", "2028-03-31", "2028-04-30"] },
        { "FREQ=MONTHLY;BYMONTHDAY=-31", "2026-01-01", "2026-01-01", "2026-03-31", ["2026-01-01", "2026-02-01", "2026-03-01"] },
        { "FREQ=MONTHLY;BYMONTHDAY=29", "2027-01-01", "2027-01-01", "2027-03-31", ["2027-01-29", "2027-02-28", "2027-03-29"] },

        // Leap days.
        { "FREQ=YEARLY", "2024-02-29", "2024-01-01", "2028-12-31", ["2024-02-29", "2025-02-28", "2026-02-28", "2027-02-28", "2028-02-29"] },
        { "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29", "2025-01-01", "2025-01-01", "2028-12-31", ["2025-02-28", "2026-02-28", "2027-02-28", "2028-02-29"] },
        { "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=-1", "2027-01-01", "2027-01-01", "2029-12-31", ["2027-02-28", "2028-02-29", "2029-02-28"] },
        { "FREQ=DAILY", "2028-02-27", "2028-02-27", "2028-03-02", ["2028-02-27", "2028-02-28", "2028-02-29", "2028-03-01", "2028-03-02"] },
        { "FREQ=WEEKLY;BYDAY=TU", "2028-02-22", "2028-02-22", "2028-03-14", ["2028-02-22", "2028-02-29", "2028-03-07", "2028-03-14"] },

        // Weekly and biweekly; the start date anchors the fortnight.
        { "FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-04", "2026-09-01", "2026-10-31", ["2026-09-04", "2026-09-18", "2026-10-02", "2026-10-16", "2026-10-30"] },
        { "FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-01", "2026-09-01", "2026-10-03", ["2026-09-04", "2026-09-18", "2026-10-02"] },
        { "FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-05", "2026-09-01", "2026-10-03", ["2026-09-18", "2026-10-02"] },
        { "FREQ=WEEKLY", "2026-09-24", "2026-09-24", "2026-10-15", ["2026-09-24", "2026-10-01", "2026-10-08", "2026-10-15"] },

        // Nth weekday.
        { "FREQ=MONTHLY;BYDAY=2TU", "2026-01-01", "2026-01-01", "2026-06-30", ["2026-01-13", "2026-02-10", "2026-03-10", "2026-04-14", "2026-05-12", "2026-06-09"] },
        { "FREQ=MONTHLY;BYDAY=-1FR", "2026-01-01", "2026-01-01", "2026-06-30", ["2026-01-30", "2026-02-27", "2026-03-27", "2026-04-24", "2026-05-29", "2026-06-26"] },
        { "FREQ=MONTHLY;BYDAY=5FR", "2026-01-01", "2026-01-01", "2026-12-31", ["2026-01-30", "2026-05-29", "2026-07-31", "2026-10-30"] },
        { "FREQ=YEARLY;BYMONTH=11;BYDAY=4TH", "2026-01-01", "2026-01-01", "2028-12-31", ["2026-11-26", "2027-11-25", "2028-11-23"] },

        // Twice monthly.
        { "FREQ=MONTHLY;BYMONTHDAY=1,15", "2026-01-10", "2026-01-01", "2026-03-31", ["2026-01-15", "2026-02-01", "2026-02-15", "2026-03-01", "2026-03-15"] },
        { "FREQ=MONTHLY;BYMONTHDAY=15,-1", "2026-01-01", "2026-01-01", "2026-03-31", ["2026-01-15", "2026-01-31", "2026-02-15", "2026-02-28", "2026-03-15", "2026-03-31"] },

        // Every N months / years; the start's month anchors the interval.
        { "FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=1", "2026-02-01", "2026-01-01", "2027-03-01", ["2026-02-01", "2026-05-01", "2026-08-01", "2026-11-01", "2027-02-01"] },
        { "FREQ=YEARLY;INTERVAL=2;BYMONTH=3;BYMONTHDAY=1", "2026-01-01", "2026-01-01", "2031-12-31", ["2026-03-01", "2028-03-01", "2030-03-01"] },
        { "FREQ=DAILY;INTERVAL=10", "2026-01-01", "2026-01-15", "2026-02-15", ["2026-01-21", "2026-01-31", "2026-02-10"] },

        // COUNT (counted from the start) and UNTIL (inclusive).
        { "FREQ=DAILY;COUNT=3", "2026-01-30", "2026-01-01", "2026-12-31", ["2026-01-30", "2026-01-31", "2026-02-01"] },
        { "FREQ=MONTHLY;BYMONTHDAY=15;COUNT=3", "2026-01-01", "2026-02-01", "2026-12-31", ["2026-02-15", "2026-03-15"] },
        { "FREQ=MONTHLY;BYMONTHDAY=15;UNTIL=20260315", "2026-01-01", "2026-01-01", "2026-12-31", ["2026-01-15", "2026-02-15", "2026-03-15"] },
        { "FREQ=MONTHLY;BYMONTHDAY=15;UNTIL=20260314", "2026-01-01", "2026-01-01", "2026-12-31", ["2026-01-15", "2026-02-15"] },

        // The start date counts only when it matches.
        { "FREQ=MONTHLY;BYMONTHDAY=15", "2026-01-20", "2026-01-01", "2026-03-31", ["2026-02-15", "2026-03-15"] },
        { "FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "2026-09-25", "2026-09-25", "2026-10-02", ["2026-09-25", "2026-09-28", "2026-09-29", "2026-09-30", "2026-10-01", "2026-10-02"] },

        // RFC 5545 section 3.8.5.3 examples (date part only).
        { "FREQ=DAILY;COUNT=10", "1997-09-02", "1997-01-01", "1997-12-31", ["1997-09-02", "1997-09-03", "1997-09-04", "1997-09-05", "1997-09-06", "1997-09-07", "1997-09-08", "1997-09-09", "1997-09-10", "1997-09-11"] },
        { "FREQ=MONTHLY;COUNT=10;BYDAY=1FR", "1997-09-05", "1997-01-01", "1998-12-31", ["1997-09-05", "1997-10-03", "1997-11-07", "1997-12-05", "1998-01-02", "1998-02-06", "1998-03-06", "1998-04-03", "1998-05-01", "1998-06-05"] },
        { "FREQ=MONTHLY;BYMONTHDAY=-3", "1997-09-28", "1997-09-01", "1998-02-28", ["1997-09-28", "1997-10-29", "1997-11-28", "1997-12-29", "1998-01-29", "1998-02-26"] },
        { "FREQ=MONTHLY;COUNT=10;BYMONTHDAY=2,15", "1997-09-02", "1997-01-01", "1998-12-31", ["1997-09-02", "1997-09-15", "1997-10-02", "1997-10-15", "1997-11-02", "1997-11-15", "1997-12-02", "1997-12-15", "1998-01-02", "1998-01-15"] },
        { "FREQ=MONTHLY;COUNT=6;BYDAY=-2MO", "1997-09-22", "1997-01-01", "1998-12-31", ["1997-09-22", "1997-10-20", "1997-11-17", "1997-12-22", "1998-01-19", "1998-02-16"] },
        { "FREQ=YEARLY;COUNT=10;BYMONTH=6,7", "1997-06-10", "1997-01-01", "2002-12-31", ["1997-06-10", "1997-07-10", "1998-06-10", "1998-07-10", "1999-06-10", "1999-07-10", "2000-06-10", "2000-07-10", "2001-06-10", "2001-07-10"] },
        { "FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=MO", "1997-08-05", "1997-01-01", "1997-12-31", ["1997-08-05", "1997-08-10", "1997-08-19", "1997-08-24"] },
        { "FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=SU", "1997-08-05", "1997-01-01", "1997-12-31", ["1997-08-05", "1997-08-17", "1997-08-19", "1997-08-31"] },
        { "FREQ=WEEKLY;UNTIL=19971007T000000Z", "1997-09-02", "1997-01-01", "1997-12-31", ["1997-09-02", "1997-09-09", "1997-09-16", "1997-09-23", "1997-09-30", "1997-10-07"] },
    };

    [Theory]
    [MemberData(nameof(OccurrenceTable))]
    public void Occurrences_match_the_table(string rule, string start, string from, string to, string[] expected)
    {
        Dates(RecurrenceRule.Parse(rule).Occurrences(D(start), D(from), D(to))).ShouldBe(expected);
    }

    [Theory]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", "2026-01-01", "2026-01-31", "2026-02-28")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", "2026-01-01", "2026-02-28", "2026-03-31")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", "2026-01-01", "2025-06-01", "2026-01-31")]
    [InlineData("FREQ=YEARLY", "2024-02-29", "2024-02-29", "2025-02-28")]
    [InlineData("FREQ=YEARLY", "2024-02-29", "2027-03-01", "2028-02-29")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-04", "2026-09-04", "2026-09-18")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-04", "2027-06-01", "2027-06-11")]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU", "2026-01-01", "2026-03-10", "2026-04-14")]
    [InlineData("FREQ=DAILY", "2026-01-01", "2026-12-31", "2027-01-01")]
    public void NextAfter_returns_the_following_occurrence(string rule, string start, string after, string expected)
    {
        RecurrenceRule.Parse(rule).NextAfter(D(start), D(after)).ShouldBe(D(expected));
    }

    [Theory]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15;COUNT=3", "2026-01-01", "2026-03-15")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15;UNTIL=20260320", "2026-01-01", "2026-03-15")]
    [InlineData("FREQ=DAILY", "2026-01-01", "9999-12-31")]
    public void NextAfter_is_null_when_the_rule_has_ended(string rule, string start, string after)
    {
        RecurrenceRule.Parse(rule).NextAfter(D(start), D(after)).ShouldBeNull();
    }

    [Fact]
    public void First_returns_the_first_occurrence_on_or_after_start()
    {
        RecurrenceRule.Parse("FREQ=MONTHLY;BYDAY=2TU").First(D("2026-01-14")).ShouldBe(D("2026-02-10"));
        RecurrenceRule.Parse("FREQ=DAILY").First(D("2026-01-14")).ShouldBe(D("2026-01-14"));
    }

    [Fact]
    public void Empty_or_inverted_windows_yield_nothing()
    {
        var rule = RecurrenceRule.Daily();
        rule.Occurrences(D("2026-01-01"), D("2026-02-01"), D("2026-01-31")).ShouldBeEmpty();
        rule.Occurrences(D("2026-03-01"), D("2026-01-01"), D("2026-02-28")).ShouldBeEmpty();
    }

    [Fact]
    public void Enumerating_to_the_end_of_the_calendar_terminates()
    {
        var rule = RecurrenceRule.Parse("FREQ=YEARLY;BYMONTH=12;BYMONTHDAY=31");
        var dates = rule.Occurrences(D("9990-01-01"), D("9990-01-01"), DateOnly.MaxValue).ToList();
        dates.Count.ShouldBe(10);
        dates[^1].ShouldBe(DateOnly.MaxValue);
        RecurrenceRule.Weekly(DayOfWeek.Friday).Occurrences(D("9999-12-01"), D("9999-12-01"), DateOnly.MaxValue).Count().ShouldBe(5);
        RecurrenceRule.MonthlyOnDay(-1).NextAfter(D("9999-01-01"), D("9999-12-31")).ShouldBeNull();
    }
}
