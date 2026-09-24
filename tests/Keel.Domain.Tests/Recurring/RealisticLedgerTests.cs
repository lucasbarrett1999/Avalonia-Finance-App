using System.Globalization;
using System.Text;
using Keel.Domain.Import;
using Keel.Domain.Recurring;
using static VerifyXunit.Verifier;

namespace Keel.Domain.Tests.Recurring;

/// <summary>Detection on the realistic fixture: exact expectations plus a reviewed golden file.</summary>
public class RealisticLedgerTests
{
    private static readonly IReadOnlyList<DetectedRecurringItem> Detected =
        RecurringDetector.Detect(RealisticLedger.Transactions(), RealisticLedger.AsOf);

    private static DetectedRecurringItem Item(string raw, Guid account) =>
        Detected.Single(d => d.NormalizedPayee == PayeeNormalizer.Normalize(raw) && d.AccountId == account);

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public void Every_labeled_group_gets_the_expected_cadence_and_irregular_spending_is_not_detected()
    {
        var (_, labels) = RealisticLedger.Build();
        foreach (var (raw, account, cadence) in labels)
        {
            var match = Detected.SingleOrDefault(d => d.NormalizedPayee == PayeeNormalizer.Normalize(raw) && d.AccountId == account);
            if (cadence is null)
            {
                match.ShouldBeNull(raw);
            }
            else
            {
                match.ShouldNotBeNull(raw);
                match.Cadence.ShouldBe(cadence.Value, raw);
            }
        }

        Detected.Count.ShouldBe(labels.Count(l => l.Cadence is not null));
    }

    [Fact]
    public void Paycheck_is_a_biweekly_inflow()
    {
        var pay = Item(RealisticLedger.PaycheckRaw, RealisticLedger.Checking);
        pay.ExpectedAmount.ShouldBe(245_000);
        pay.NextExpectedDate.ShouldBe(D("2026-10-02"));
        pay.Confidence.ShouldBe(1.0);
        pay.IsInflow.ShouldBeTrue();
    }

    [Fact]
    public void Rent_stays_on_the_first_despite_an_early_payment()
    {
        var rent = Item(RealisticLedger.RentRaw, RealisticLedger.Checking);
        rent.NextExpectedDate.ShouldBe(D("2026-10-01"));
        rent.Rule.ToString().ShouldBe("FREQ=MONTHLY;BYMONTHDAY=1");
        rent.Confidence.ShouldBe(1.0);
    }

    [Fact]
    public void Netflix_after_its_price_increase_expects_the_new_price()
    {
        var netflix = Item(RealisticLedger.NetflixRaw, RealisticLedger.Visa);
        netflix.ExpectedAmount.ShouldBe(-1_799);
        netflix.IsVariableAmount.ShouldBeTrue(); // the $15.49 month is still among the last six
        netflix.Confidence.ShouldBe(0.8);
        netflix.NextExpectedDate.ShouldBe(D("2026-10-12"));
    }

    [Fact]
    public void Quarterly_annual_utility_and_cancelled_items()
    {
        Item(RealisticLedger.InsuranceRaw, RealisticLedger.Checking).NextExpectedDate.ShouldBe(D("2026-10-15"));
        Item(RealisticLedger.DomainRaw, RealisticLedger.Visa).NextExpectedDate.ShouldBe(D("2027-08-04"));
        var utility = Item(RealisticLedger.UtilityRaw, RealisticLedger.Checking);
        utility.IsVariableAmount.ShouldBeTrue();
        utility.Confidence.ShouldBe(0.8);
        Item(RealisticLedger.GymRaw, RealisticLedger.Checking).IsLapsed.ShouldBeTrue();
    }

    [Fact]
    public void Stored_items_project_with_the_same_rule_as_detection()
    {
        foreach (var item in Detected)
        {
            RecurringSchedule.InferRule(item.Cadence, item.NextExpectedDate, item.LastSeenDate).ShouldBe(item.Rule, item.NormalizedPayee);
        }
    }

    [Fact]
    public Task Golden_detections() => Verify(Print(Detected));

    internal static string Print(IEnumerable<DetectedRecurringItem> items)
    {
        var sb = new StringBuilder();
        foreach (var d in items)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{d.NormalizedPayee} @ {RealisticLedger.Name(d.AccountId)}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  cadence {d.Cadence}, rule {d.Rule} ({d.Rule.Describe(d.NextExpectedDate)})").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  expected {Money(d.ExpectedAmount)} +/- {Money(d.AmountTolerance)}{(d.IsVariableAmount ? ", variable" : string.Empty)}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  seen {d.FirstSeenDate:yyyy-MM-dd} .. {d.LastSeenDate:yyyy-MM-dd} ({d.OccurrenceCount} occurrences), next {d.NextExpectedDate:yyyy-MM-dd}{(d.IsLapsed ? ", LAPSED" : string.Empty)}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  confidence {d.Confidence:F3} (fraction {d.CadenceFraction:F3})").AppendLine();
            sb.Append("  scores:");
            foreach (var s in d.Scores)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {s.Cadence} {s.Fraction:F2}/{s.MeanResidualDays:F1}d;");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string Money(long minor) =>
        (minor / 100m).ToString("#,##0.00;-#,##0.00;0.00", CultureInfo.InvariantCulture);
}
