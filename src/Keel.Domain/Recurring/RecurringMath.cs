using Keel.Domain.Budgeting;
using Keel.Domain.Entities;

namespace Keel.Domain.Recurring;

/// <summary>An item as the Bills totals see it (F-REC-2).</summary>
/// <param name="ExpectedAmount">Signed expected amount per occurrence (minor units).</param>
/// <param name="Cadence">Cadence.</param>
/// <param name="Status">Status; only Active and (optionally) Detected items count.</param>
/// <param name="IsSubscription">Subscription rather than bill (see <see cref="SubscriptionClassifier"/>).</param>
public sealed record RecurringTotalsItem(long ExpectedAmount, RecurrenceCadence Cadence, RecurringStatus Status, bool IsSubscription)
{
    /// <summary>Projects a stored item.</summary>
    public static RecurringTotalsItem From(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new RecurringTotalsItem(item.ExpectedAmount, item.Cadence, item.Status, item.IsSubscription);
    }
}

/// <summary>
/// Bills and subscriptions totals (F-REC-2). Every value is a sum of per-item monthly (or yearly)
/// equivalents, signed: outflow totals are negative, inflow totals positive.
/// </summary>
/// <param name="MonthlyOutflow">Monthly recurring outflow (bills and subscriptions).</param>
/// <param name="MonthlyInflow">Monthly recurring inflow (paychecks).</param>
/// <param name="SubscriptionsMonthly">Monthly equivalent of subscription outflows.</param>
/// <param name="SubscriptionsYearly">Yearly equivalent of subscription outflows.</param>
/// <param name="BillsMonthly">Monthly equivalent of non-subscription outflows.</param>
/// <param name="BillsYearly">Yearly equivalent of non-subscription outflows.</param>
/// <param name="ItemCount">Items counted.</param>
public sealed record RecurringTotals(
    long MonthlyOutflow,
    long MonthlyInflow,
    long SubscriptionsMonthly,
    long SubscriptionsYearly,
    long BillsMonthly,
    long BillsYearly,
    int ItemCount);

/// <summary>
/// Cadence arithmetic for recurring items: monthly and yearly equivalents (F-REC-2 totals) and the
/// monthly set-aside target of an item (F-REC-4, "true expenses"). Integer minor units only.
/// </summary>
public static class RecurringMath
{
    /// <summary>Occurrences per year: weekly 52, biweekly 26, semimonthly 24, monthly 12, quarterly 4, yearly 1.</summary>
    public static int OccurrencesPerYear(RecurrenceCadence cadence) => cadence switch
    {
        RecurrenceCadence.Weekly => 52,
        RecurrenceCadence.Biweekly => 26,
        RecurrenceCadence.Semimonthly => 24,
        RecurrenceCadence.Monthly => 12,
        RecurrenceCadence.Quarterly => 4,
        RecurrenceCadence.Yearly => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown cadence."),
    };

    /// <summary>Amount per year: <paramref name="amount"/> × occurrences per year.</summary>
    public static long Annualized(long amount, RecurrenceCadence cadence) => checked(amount * OccurrencesPerYear(cadence));

    /// <summary>
    /// Monthly equivalent: annualized amount / 12, rounded half to even (weekly $10 is $43.33,
    /// quarterly $90 is $30, yearly $100 is $8.33). Used for totals and for the list's monthly column.
    /// </summary>
    public static long MonthlyEquivalent(long amount, RecurrenceCadence cadence) =>
        QuickAssign.DivideHalfEven(Annualized(amount, cadence), 12);

    /// <summary>
    /// The monthly set-aside for an item (F-REC-4): the magnitude of the annualized amount / 12,
    /// rounded up, so twelve set-asides always cover a year of occurrences (yearly $100 is $8.34).
    /// Always ≥ 0.
    /// </summary>
    public static long MonthlySetAside(long expectedAmount, RecurrenceCadence cadence)
    {
        var yearly = Math.Abs(Annualized(expectedAmount, cadence));
        return (yearly / 12) + (yearly % 12 == 0 ? 0 : 1);
    }

    /// <summary>
    /// The F-REC-4 target: a monthly set-aside on <paramref name="categoryId"/> equal to the item's
    /// monthly set-aside (annualized for non-monthly cadences).
    /// </summary>
    public static Target CreateSetAsideTarget(Guid categoryId, long expectedAmount, RecurrenceCadence cadence) => new()
    {
        CategoryId = categoryId,
        Type = TargetType.MonthlySetAside,
        Amount = MonthlySetAside(expectedAmount, cadence),
        Cadence = RecurrenceCadence.Monthly,
    };

    /// <summary>
    /// Totals over <paramref name="items"/> (F-REC-2). Paused, ended and dismissed items never count;
    /// detected (unconfirmed) items count unless <paramref name="includeDetected"/> is false.
    /// </summary>
    public static RecurringTotals Totals(IEnumerable<RecurringTotalsItem> items, bool includeDetected = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        long outflow = 0, inflow = 0, subsMonthly = 0, subsYearly = 0, billsMonthly = 0, billsYearly = 0;
        var count = 0;
        foreach (var item in items)
        {
            if (item.Status != RecurringStatus.Active && !(includeDetected && item.Status == RecurringStatus.Detected))
            {
                continue;
            }

            count++;
            var monthly = MonthlyEquivalent(item.ExpectedAmount, item.Cadence);
            if (item.ExpectedAmount >= 0)
            {
                inflow += monthly;
                continue;
            }

            outflow += monthly;
            if (item.IsSubscription)
            {
                subsMonthly += monthly;
                subsYearly += Annualized(item.ExpectedAmount, item.Cadence);
            }
            else
            {
                billsMonthly += monthly;
                billsYearly += Annualized(item.ExpectedAmount, item.Cadence);
            }
        }

        return new RecurringTotals(outflow, inflow, subsMonthly, subsYearly, billsMonthly, billsYearly, count);
    }
}
