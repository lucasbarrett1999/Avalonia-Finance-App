using System.Globalization;
using System.Text;
using Keel.Domain.Forecast;
using Keel.Domain.Import;
using Keel.Domain.Tests.Recurring;
using static VerifyXunit.Verifier;

namespace Keel.Domain.Tests.Forecast;

/// <summary>
/// M5 exit criterion: forecast golden tests. The golden files print every day of every series with
/// what lands on it, the discretionary-spend computation, skipped sources, low points and floor days.
/// </summary>
public class ForecastGoldenTests
{
    [Fact]
    public Task Realistic_90_days_with_discretionary_spend_and_floor()
    {
        var input = ForecastScenario.Build();
        var result = ForecastEngine.Compute(input, ForecastScenario.Options);

        // The rent is scheduled and also a confirmed recurring item: it lands once per month.
        result.Entries.Count(e => e.Label?.Contains("RENT", StringComparison.OrdinalIgnoreCase) == true).ShouldBe(3);
        result.Skipped.ShouldContain(s => s.Reason == ForecastSkipReason.CoveredBySchedule && s.Label == PayeeNormalizer.Normalize(RealisticLedger.RentRaw));

        // The car payment due on the 20th was not entered: it lands today.
        result.Explain(RealisticLedger.AsOf, RealisticLedger.Checking).Entries.ShouldContain(e => e.Label == "Car loan" && e.IsOverdue);

        // Floor detection.
        var checking = result.Series(RealisticLedger.Checking);
        checking.BelowFloor.ShouldNotBeEmpty();
        checking.BelowFloor.ShouldAllBe(d => d.Balance < 50_000);
        checking.Lowest.Balance.ShouldBe(checking.Days.Min(d => d.Balance));

        return Verify(Print(result));
    }

    [Fact]
    public Task Realistic_90_days_scheduled_and_recurring_only()
    {
        var result = ForecastEngine.Compute(ForecastScenario.Build(), ForecastScenario.Options with { IncludeDiscretionarySpend = false });
        result.Discretionary.ShouldBeEmpty();
        return Verify(Print(result));
    }

    [Fact]
    public Task Double_count_guard_off_when_the_schedule_is_removed()
    {
        var input = ForecastScenario.Build();
        var withoutRentSchedule = input with { Scheduled = input.Scheduled.Where(s => s.Label != "Rent (scheduled)").ToList() };
        var guarded = ForecastEngine.Compute(input, ForecastScenario.Options);
        var recurringOnly = ForecastEngine.Compute(withoutRentSchedule, ForecastScenario.Options);

        // Same rent either way: counted exactly once.
        recurringOnly.Series(RealisticLedger.Checking).Days[^1].Balance.ShouldBe(guarded.Series(RealisticLedger.Checking).Days[^1].Balance);
        recurringOnly.Skipped.ShouldNotContain(s => s.Reason == ForecastSkipReason.CoveredBySchedule);
        return Verify(Print(recurringOnly));
    }

    internal static string Print(ForecastResult result)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Forecast {result.Start:yyyy-MM-dd} .. {result.End:yyyy-MM-dd}, floor {(result.Floor is { } f ? Money(f) : "none")}").AppendLine();
        foreach (var d in result.Discretionary)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"Discretionary {Name(result, d.AccountId)}: {Money(d.TotalSpend)} over {d.Days} days ({d.From:yyyy-MM-dd} .. {d.To:yyyy-MM-dd}, {d.TransactionCount} counted, {d.ExcludedCount} excluded) = {Money(d.Daily)} per day")
                .AppendLine();
        }

        foreach (var s in result.Skipped)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Skipped {s.Source} {s.Label}: {s.Reason}").AppendLine();
        }

        foreach (var series in result.Accounts.Append(result.Combined))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{series.Name}: start {Money(series.StartingBalance)}, end {Money(series.Days[^1].Balance)}, lowest {Money(series.Lowest.Balance)} on {series.Lowest.Date:yyyy-MM-dd}, below floor on {series.BelowFloor.Count} days{Ranges(series.BelowFloor.Select(d => d.Date).ToList())}")
                .AppendLine();
        }

        sb.AppendLine();
        sb.Append(Col("Date", 12));
        foreach (var series in result.Accounts)
        {
            sb.Append(Col(series.Name, 12, right: true));
        }

        sb.Append(Col("Combined", 12, right: true)).AppendLine("  Entries");
        for (var i = 0; i < result.Combined.Days.Count; i++)
        {
            var date = result.Combined.Days[i].Date;
            sb.Append(Col(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 12));
            foreach (var series in result.Accounts)
            {
                sb.Append(Col(Money(series.Days[i].Balance), 12, right: true));
            }

            sb.Append(Col(Money(result.Combined.Days[i].Balance), 12, right: true));
            var entries = result.Explain(date).Entries.Where(e => e.Kind != ForecastEntryKind.Discretionary).ToList();
            if (entries.Count > 0)
            {
                sb.Append("  ").AppendJoin("; ", entries.Select(e =>
                    $"{Name(result, e.AccountId)} {e.Label} {Money(e.Amount)}{(e.IsOverdue ? $" (overdue, due {e.DueDate:yyyy-MM-dd})" : string.Empty)}"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Name(ForecastResult result, Guid accountId) =>
        result.Accounts.FirstOrDefault(a => a.AccountId == accountId)?.Name ?? accountId.ToString();

    private static string Ranges(IReadOnlyList<DateOnly> dates)
    {
        if (dates.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var first = dates[0];
        var prev = first;
        foreach (var d in dates.Skip(1).Append(DateOnly.MaxValue))
        {
            if (d != DateOnly.MaxValue && d.DayNumber == prev.DayNumber + 1)
            {
                prev = d;
                continue;
            }

            parts.Add(first == prev
                ? first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : $"{first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}..{prev.ToString("MM-dd", CultureInfo.InvariantCulture)}");
            first = prev = d;
        }

        return ": " + string.Join(", ", parts);
    }

    private static string Col(string text, int width, bool right = false) =>
        right ? text.PadLeft(width) : text.PadRight(width);

    private static string Money(long minor) => RealisticLedgerTests.Money(minor);
}
