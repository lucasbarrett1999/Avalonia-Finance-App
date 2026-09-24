using Keel.Domain.Entities;
using Keel.Domain.Recurring;

namespace Keel.Domain.Tests.Recurring;

public class RecurringMathTests
{
    [Theory]
    [InlineData(RecurrenceCadence.Weekly, 52)]
    [InlineData(RecurrenceCadence.Biweekly, 26)]
    [InlineData(RecurrenceCadence.Semimonthly, 24)]
    [InlineData(RecurrenceCadence.Monthly, 12)]
    [InlineData(RecurrenceCadence.Quarterly, 4)]
    [InlineData(RecurrenceCadence.Yearly, 1)]
    public void Occurrences_per_year(RecurrenceCadence cadence, int perYear) =>
        RecurringMath.OccurrencesPerYear(cadence).ShouldBe(perYear);

    [Theory]
    [InlineData(-1_000, RecurrenceCadence.Weekly, -4_333, -52_000)]     // 43.333
    [InlineData(245_000, RecurrenceCadence.Biweekly, 530_833, 6_370_000)]
    [InlineData(180_000, RecurrenceCadence.Semimonthly, 360_000, 4_320_000)]
    [InlineData(-1_549, RecurrenceCadence.Monthly, -1_549, -18_588)]
    [InlineData(-31_240, RecurrenceCadence.Quarterly, -10_413, -124_960)] // 104.1333
    [InlineData(-10_000, RecurrenceCadence.Yearly, -833, -10_000)]        // 8.333
    [InlineData(-1_498, RecurrenceCadence.Yearly, -125, -1_498)]          // 1.2483
    [InlineData(-18, RecurrenceCadence.Yearly, -2, -18)]                  // 1.5 rounds half to even
    [InlineData(-30, RecurrenceCadence.Yearly, -2, -30)]                  // 2.5 rounds half to even
    public void Monthly_and_yearly_equivalents(long amount, RecurrenceCadence cadence, long monthly, long yearly)
    {
        RecurringMath.MonthlyEquivalent(amount, cadence).ShouldBe(monthly);
        RecurringMath.Annualized(amount, cadence).ShouldBe(yearly);
    }

    [Theory]
    [InlineData(-10_000, RecurrenceCadence.Yearly, 834)]      // 12 x 8.34 covers 100.00
    [InlineData(-31_240, RecurrenceCadence.Quarterly, 10_414)]
    [InlineData(-1_549, RecurrenceCadence.Monthly, 1_549)]
    [InlineData(-1_000, RecurrenceCadence.Weekly, 4_334)]
    [InlineData(-12_000, RecurrenceCadence.Yearly, 1_000)]
    [InlineData(5_000, RecurrenceCadence.Monthly, 5_000)]
    [InlineData(0, RecurrenceCadence.Monthly, 0)]
    public void Set_aside_is_rounded_up_so_a_year_is_covered(long amount, RecurrenceCadence cadence, long setAside)
    {
        var value = RecurringMath.MonthlySetAside(amount, cadence);
        value.ShouldBe(setAside);
        (value * 12).ShouldBeGreaterThanOrEqualTo(Math.Abs(RecurringMath.Annualized(amount, cadence)));
    }

    [Fact]
    public void Creates_a_monthly_set_aside_target()
    {
        var category = RecurringSeries.Id(7, 1);
        var target = RecurringMath.CreateSetAsideTarget(category, -31_240, RecurrenceCadence.Quarterly);
        target.CategoryId.ShouldBe(category);
        target.Type.ShouldBe(TargetType.MonthlySetAside);
        target.Amount.ShouldBe(10_414);
        target.Cadence.ShouldBe(RecurrenceCadence.Monthly);
        target.TargetDate.ShouldBeNull();
    }

    [Fact]
    public void Totals_split_inflow_bills_and_subscriptions()
    {
        RecurringTotalsItem[] items =
        [
            new(245_000, RecurrenceCadence.Biweekly, RecurringStatus.Active, false),     // +5,308.33
            new(-185_000, RecurrenceCadence.Monthly, RecurringStatus.Active, false),     // bill
            new(-1_799, RecurrenceCadence.Monthly, RecurringStatus.Active, true),        // subscription
            new(-1_498, RecurrenceCadence.Yearly, RecurringStatus.Detected, true),       // subscription, unconfirmed
            new(-31_240, RecurrenceCadence.Quarterly, RecurringStatus.Active, false),    // bill
            new(-2_499, RecurrenceCadence.Monthly, RecurringStatus.Paused, true),        // ignored
            new(-9_999, RecurrenceCadence.Monthly, RecurringStatus.Ended, false),         // ignored
            new(-9_999, RecurrenceCadence.Monthly, RecurringStatus.Dismissed, false),    // ignored
        ];

        var totals = RecurringMath.Totals(items);
        totals.ItemCount.ShouldBe(5);
        totals.MonthlyInflow.ShouldBe(530_833);
        totals.MonthlyOutflow.ShouldBe(-185_000 - 1_799 - 125 - 10_413);
        totals.SubscriptionsMonthly.ShouldBe(-1_799 - 125);
        totals.SubscriptionsYearly.ShouldBe(-21_588 - 1_498);
        totals.BillsMonthly.ShouldBe(-185_000 - 10_413);
        totals.BillsYearly.ShouldBe(-2_220_000 - 124_960);
        (totals.SubscriptionsMonthly + totals.BillsMonthly).ShouldBe(totals.MonthlyOutflow);

        var confirmed = RecurringMath.Totals(items, includeDetected: false);
        confirmed.ItemCount.ShouldBe(4);
        confirmed.SubscriptionsMonthly.ShouldBe(-1_799);
    }

    [Fact]
    public void Totals_item_from_entity()
    {
        var item = new RecurringItem { ExpectedAmount = -1_799, Cadence = RecurrenceCadence.Monthly, Status = RecurringStatus.Active, IsSubscription = true };
        RecurringTotalsItem.From(item).ShouldBe(new RecurringTotalsItem(-1_799, RecurrenceCadence.Monthly, RecurringStatus.Active, true));
    }

    [Fact]
    public void Subscription_by_category_group_or_tag()
    {
        var subsGroup = RecurringSeries.Id(8, 1);
        var billsGroup = RecurringSeries.Id(8, 2);
        var streaming = new Category { Id = RecurringSeries.Id(9, 1), GroupId = subsGroup, Name = "Streaming" };
        var rent = new Category { Id = RecurringSeries.Id(9, 2), GroupId = billsGroup, Name = "Rent" };
        var subscriptionTag = RecurringSeries.Id(10, 1);
        var otherTag = RecurringSeries.Id(10, 2);
        var hints = SubscriptionHints.Build([streaming, rent], [subsGroup], [subscriptionTag]);

        SubscriptionClassifier.IsSubscription(streaming.Id, [], hints).ShouldBeTrue();
        SubscriptionClassifier.IsSubscription(rent.Id, [], hints).ShouldBeFalse();
        SubscriptionClassifier.IsSubscription(rent.Id, [otherTag, subscriptionTag], hints).ShouldBeTrue();
        SubscriptionClassifier.IsSubscription(null, [otherTag], hints).ShouldBeFalse();
        SubscriptionClassifier.IsSubscription(streaming.Id, [], SubscriptionHints.None).ShouldBeFalse();
    }
}
