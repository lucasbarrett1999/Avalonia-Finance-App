using Keel.Domain.Forecast;
using Keel.Domain.Import;
using Keel.Domain.Recurring;
using Keel.Domain.Scheduling;
using Keel.Domain.Tests.Recurring;

namespace Keel.Domain.Tests.Forecast;

/// <summary>
/// The realistic ledger as of 2026-09-24 turned into a forecast input: detected items confirmed
/// (Spotify left unconfirmed), rent also scheduled (double-count guard), a monthly transfer to
/// savings, a car payment due on the 20th that was not entered yet (overdue), a dentist plan that
/// ends in November, and 90 days of history for discretionary spending.
/// </summary>
public static class ForecastScenario
{
    public static readonly Guid Brokerage = RecurringSeries.Account(9);
    public static readonly Guid TransferPayee = RecurringSeries.Id(40, 1);
    public static readonly Guid CarLoanPayee = RecurringSeries.Id(40, 2);
    public static readonly Guid DentistPayee = RecurringSeries.Id(40, 3);

    public static readonly ForecastOptions Options = new(Days: 90, IncludeDiscretionarySpend: true, Floor: 50_000);

    /// <summary>A stable payee id per normalized payee (what the Payee table would hold).</summary>
    public static Guid PayeeId(string normalized)
    {
        var hash = 17;
        foreach (var c in normalized)
        {
            hash = unchecked((hash * 31) + c);
        }

        return RecurringSeries.Id(41, hash & 0x7FFFFFFF);
    }

    public static string Label(string normalized) => normalized;

    public static ForecastInput Build()
    {
        var today = RealisticLedger.AsOf;
        var ledger = RealisticLedger.Transactions();
        var detections = RecurringDetector.Detect(ledger, today);
        var id = 0;
        var decisions = RecurringReconciler.Reconcile(detections, [], newId: () => RecurringSeries.Id(42, ++id));
        var spotify = PayeeNormalizer.Normalize(RealisticLedger.SpotifyRaw);
        var recurring = decisions
            .Where(d => d.Kind == RecurringDecisionKind.Create)
            .Select(d => new ForecastRecurring(
                d.ItemId!.Value,
                PayeeId(d.Detection.NormalizedPayee),
                d.After!.AccountId,
                d.After.Cadence,
                d.After.ExpectedAmount,
                d.After.NextExpectedDate,
                d.After.LastSeenDate,
                d.Detection.NormalizedPayee == spotify ? RecurringStatus.Detected : RecurringStatus.Active,
                null,
                null,
                Label(d.Detection.NormalizedPayee)))
            .ToList();

        var rentPayee = PayeeId(PayeeNormalizer.Normalize(RealisticLedger.RentRaw));
        ForecastScheduled[] scheduled =
        [
            new(RecurringSeries.Id(43, 1), RealisticLedger.Checking, rentPayee, -185_000, RecurrenceRule.MonthlyOnDay(1), new DateOnly(2026, 10, 1), Label: "Rent (scheduled)"),
            new(RecurringSeries.Id(43, 2), RealisticLedger.Checking, TransferPayee, -30_000, RecurrenceRule.MonthlyOnDay(5), new DateOnly(2026, 10, 5), TransferAccountId: RealisticLedger.Savings, Label: "Transfer to savings"),
            new(RecurringSeries.Id(43, 3), RealisticLedger.Checking, CarLoanPayee, -38_900, RecurrenceRule.MonthlyOnDay(20), new DateOnly(2026, 9, 20), Label: "Car loan"),
            new(RecurringSeries.Id(43, 4), RealisticLedger.Checking, DentistPayee, -12_000, RecurrenceRule.Parse("FREQ=MONTHLY;BYMONTHDAY=10;UNTIL=20261110"), new DateOnly(2026, 10, 10), Label: "Dentist plan"),
        ];

        ForecastAccount[] accounts =
        [
            new(RealisticLedger.Checking, "Checking", AccountType.Checking, true, 215_000),
            new(RealisticLedger.Savings, "Savings", AccountType.Savings, true, 800_000),
            new(RealisticLedger.Visa, "Visa", AccountType.CreditCard, true, -42_000),
            new(Brokerage, "Brokerage", AccountType.Investment, false, 5_000_000),
        ];

        var history = ledger
            .Select(t => new ForecastHistoryTransaction(t.Id, t.AccountId, t.Date, t.Amount, PayeeId(t.NormalizedPayee), false))
            .Append(new ForecastHistoryTransaction(RecurringSeries.Id(44, 1), RealisticLedger.Checking, new DateOnly(2026, 9, 5), -30_000, TransferPayee, true))
            .Append(new ForecastHistoryTransaction(RecurringSeries.Id(44, 2), RealisticLedger.Checking, new DateOnly(2026, 8, 20), -38_900, CarLoanPayee, false, RecurringSeries.Id(43, 3)))
            .ToList();

        return new ForecastInput(today, accounts, scheduled, recurring, history);
    }
}
