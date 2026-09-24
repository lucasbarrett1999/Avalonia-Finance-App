using Keel.Domain.Recurring;

namespace Keel.Domain.Alerts;

/// <summary>Everything alert evaluation looks at.</summary>
/// <param name="Today">The evaluation date ("now").</param>
/// <param name="Items">Stored recurring items with their normalized payees (as they were before the batch).</param>
/// <param name="History">Existing transactions of the relevant payees (at least the last few of each group).</param>
/// <param name="Batch">Newly imported or entered transactions (may be empty for a date-only run).</param>
/// <param name="Decisions">Reconciliation decisions of the detection run that accompanies this evaluation.</param>
/// <param name="ExistingKeys">Keys of every stored alert, including read and dismissed ones.</param>
public sealed record AlertEvaluationInput(
    DateOnly Today,
    IReadOnlyList<AlertRecurringItem> Items,
    IReadOnlyList<RecurringTransaction> History,
    IReadOnlyList<RecurringTransaction> Batch,
    IReadOnlyList<RecurringDecision> Decisions,
    IReadOnlySet<string> ExistingKeys);

/// <summary>
/// Notification-center alerts (F-REC-3, PRD 6.6): price increase, expected item missing, new recurring
/// item, and first charge after a trial. Pure and idempotent: a proposal's key identifies its (kind,
/// item, occurrence), and keys already stored (or proposed earlier in the same run) are never proposed
/// again. Interpretations are in <c>docs/decisions/0032-recurring-alert-rules.md</c>.
/// </summary>
public static class AlertEvaluator
{
    /// <summary>An expected item is missing this many days after its expected date.</summary>
    public const int MissingAfterDays = 3;

    /// <summary>A price increase must exceed this many minor units ($1).</summary>
    public const long PriceIncreaseMinimum = 100;

    /// <summary>A price increase must exceed this percentage of the previous amount.</summary>
    public const int PriceIncreasePercent = 5;

    /// <summary>A charge of at most this magnitude ($1.00), or $0, is a trial charge.</summary>
    public const long TrialMaxAmount = 100;

    /// <summary>The real charge must follow the trial charge within this many days.</summary>
    public const int TrialWindowDays = 90;

    /// <summary>Runs every rule and returns the new proposals in a stable order (kind, date, key).</summary>
    public static IReadOnlyList<AlertProposal> Evaluate(AlertEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var seen = new HashSet<string>(input.ExistingKeys, StringComparer.Ordinal);
        var result = new List<AlertProposal>();
        foreach (var proposal in PriceIncreases(input.Items, input.History, input.Batch)
            .Concat(MissingExpected(input.Items, input.History, input.Batch, input.Today))
            .Concat(NewRecurring(input.Decisions))
            .Concat(TrialConversions(input.Items, input.History, input.Batch)))
        {
            if (seen.Add(proposal.Key))
            {
                result.Add(proposal);
            }
        }

        return result
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Payload.Date ?? p.Payload.ExpectedDate ?? p.Payload.NextExpectedDate)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True when an outflow of <paramref name="current"/> is a price increase over <paramref name="previous"/>
    /// (both signed, outflows negative): larger by more than 5% and by more than $1.
    /// </summary>
    public static bool IsPriceIncrease(long previous, long current)
    {
        if (previous >= 0 || current >= 0)
        {
            return false;
        }

        var before = -previous;
        var increase = -current - before;
        return increase > PriceIncreaseMinimum && increase * 100 > before * PriceIncreasePercent;
    }

    /// <summary>
    /// Price increases (F-REC-3, 6.6): each batch outflow of an active or detected, fixed-amount item is
    /// compared with the previous outflow of the same payee and account (or, without one, the item's
    /// expected amount).
    /// </summary>
    public static IEnumerable<AlertProposal> PriceIncreases(
        IReadOnlyList<AlertRecurringItem> items,
        IReadOnlyList<RecurringTransaction> history,
        IReadOnlyList<RecurringTransaction> batch)
    {
        ArgumentNullException.ThrowIfNull(items);
        var all = Timeline(history, batch);
        foreach (var t in SortedBatch(batch))
        {
            if (t.Amount >= 0)
            {
                continue;
            }

            var item = FindItem(items, t, i => i.Status is RecurringStatus.Active or RecurringStatus.Detected && !i.IsVariableAmount);
            if (item is null)
            {
                continue;
            }

            var previous = Previous(all, t, p => p.AccountId == t.AccountId && p.Amount < 0)?.Amount
                ?? (item.ExpectedAmount < 0 ? item.ExpectedAmount : (long?)null);
            if (previous is not { } before || !IsPriceIncrease(before, t.Amount))
            {
                continue;
            }

            var key = AlertKeys.PriceIncrease(item.Id, t.Id);
            yield return new AlertProposal(
                AlertKind.PriceIncrease,
                key,
                item.Id,
                t.Id,
                new AlertPayload(
                    key,
                    Payee: t.NormalizedPayee,
                    Date: t.Date,
                    Amount: t.Amount,
                    PreviousAmount: before,
                    IncreaseBasisPoints: (before - t.Amount) * 10_000 / -before));
        }
    }

    /// <summary>
    /// Missing items (F-REC-3): an active item is missing when <paramref name="today"/> is at least
    /// 3 days past its next expected date and no transaction of the item (same direction) arrived since
    /// the cadence's early window before that date.
    /// </summary>
    public static IEnumerable<AlertProposal> MissingExpected(
        IReadOnlyList<AlertRecurringItem> items,
        IReadOnlyList<RecurringTransaction> history,
        IReadOnlyList<RecurringTransaction> batch,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(items);
        var all = Timeline(history, batch);
        foreach (var item in items.OrderBy(i => i.NextExpectedDate).ThenBy(i => i.Id))
        {
            var daysLate = today.DayNumber - item.NextExpectedDate.DayNumber;
            if (item.Status != RecurringStatus.Active || daysLate < MissingAfterDays || item.ExpectedAmount == 0)
            {
                continue;
            }

            var earliest = item.NextExpectedDate.AddDays(-CadenceWindow.For(item.Cadence).ToleranceDays);
            if (earliest <= item.LastSeenDate)
            {
                earliest = item.LastSeenDate.AddDays(1);
            }

            var arrived = all.TryGetValue(item.NormalizedPayee, out var payeeRows) && payeeRows.Any(t =>
                t.Date >= earliest
                && t.Date <= today
                && t.Amount != 0
                && (t.Amount < 0) == (item.ExpectedAmount < 0)
                && item.Matches(t.NormalizedPayee, t.AccountId));
            if (arrived)
            {
                continue;
            }

            var key = AlertKeys.MissingExpected(item.Id, item.NextExpectedDate);
            yield return new AlertProposal(
                AlertKind.MissingExpected,
                key,
                item.Id,
                null,
                new AlertPayload(
                    key,
                    Payee: item.NormalizedPayee,
                    Amount: item.ExpectedAmount,
                    ExpectedDate: item.NextExpectedDate,
                    DaysLate: daysLate));
        }
    }

    /// <summary>New recurring items (F-REC-3): one alert per item a detection run creates.</summary>
    public static IEnumerable<AlertProposal> NewRecurring(IReadOnlyList<RecurringDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        foreach (var d in decisions)
        {
            if (d.Kind != RecurringDecisionKind.Create || d.ItemId is not { } id)
            {
                continue;
            }

            var key = AlertKeys.NewRecurring(id);
            yield return new AlertProposal(
                AlertKind.NewRecurring,
                key,
                id,
                null,
                new AlertPayload(
                    key,
                    Payee: d.Detection.NormalizedPayee,
                    Amount: d.Detection.ExpectedAmount,
                    Cadence: d.Detection.Cadence,
                    NextExpectedDate: d.Detection.NextExpectedDate));
        }
    }

    /// <summary>
    /// Trial conversions (F-REC-3): a batch outflow larger than $1 whose previous outflow from the same
    /// payee (any account) was a $0 or at most $1 trial charge within the last 90 days.
    /// </summary>
    public static IEnumerable<AlertProposal> TrialConversions(
        IReadOnlyList<AlertRecurringItem> items,
        IReadOnlyList<RecurringTransaction> history,
        IReadOnlyList<RecurringTransaction> batch)
    {
        ArgumentNullException.ThrowIfNull(items);
        var all = Timeline(history, batch);
        foreach (var t in SortedBatch(batch))
        {
            if (-t.Amount <= TrialMaxAmount)
            {
                continue; // not an outflow, or itself a trial-sized charge
            }

            var previous = Previous(all, t, p => p.Amount <= 0);
            if (previous is null || -previous.Amount > TrialMaxAmount || t.Date.DayNumber - previous.Date.DayNumber > TrialWindowDays)
            {
                continue;
            }

            var item = FindItem(items, t, i => i.Status != RecurringStatus.Dismissed);
            var key = AlertKeys.TrialConversion(t.Id);
            yield return new AlertProposal(
                AlertKind.TrialConversion,
                key,
                item?.Id,
                t.Id,
                new AlertPayload(
                    key,
                    Payee: t.NormalizedPayee,
                    Date: t.Date,
                    Amount: t.Amount,
                    PreviousAmount: previous.Amount,
                    TrialTransactionId: previous.Id));
        }
    }

    // History and batch merged per payee, ordered by (date, id); a row present in both counts once.
    private static Dictionary<string, List<RecurringTransaction>> Timeline(
        IReadOnlyList<RecurringTransaction> history,
        IReadOnlyList<RecurringTransaction> batch)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(batch);
        var ids = new HashSet<Guid>();
        var byPayee = new Dictionary<string, List<RecurringTransaction>>(StringComparer.Ordinal);
        foreach (var t in history.Concat(batch))
        {
            if (!ids.Add(t.Id))
            {
                continue;
            }

            if (!byPayee.TryGetValue(t.NormalizedPayee, out var list))
            {
                list = [];
                byPayee[t.NormalizedPayee] = list;
            }

            list.Add(t);
        }

        foreach (var list in byPayee.Values)
        {
            list.Sort(Compare);
        }

        return byPayee;
    }

    private static IEnumerable<RecurringTransaction> SortedBatch(IReadOnlyList<RecurringTransaction> batch)
    {
        var list = batch.DistinctBy(t => t.Id).ToList();
        list.Sort(Compare);
        return list;
    }

    private static RecurringTransaction? Previous(
        Dictionary<string, List<RecurringTransaction>> timeline,
        RecurringTransaction t,
        Func<RecurringTransaction, bool> predicate)
    {
        if (!timeline.TryGetValue(t.NormalizedPayee, out var list))
        {
            return null;
        }

        RecurringTransaction? previous = null;
        foreach (var p in list)
        {
            if (Compare(p, t) >= 0)
            {
                break;
            }

            if (predicate(p))
            {
                previous = p;
            }
        }

        return previous;
    }

    private static AlertRecurringItem? FindItem(
        IReadOnlyList<AlertRecurringItem> items,
        RecurringTransaction t,
        Func<AlertRecurringItem, bool> predicate) =>
        items.Where(i => predicate(i) && i.Matches(t.NormalizedPayee, t.AccountId))
            .OrderBy(i => i.AccountId is null ? 1 : 0)
            .ThenBy(i => i.Id)
            .FirstOrDefault();

    private static int Compare(RecurringTransaction a, RecurringTransaction b) =>
        a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Id.CompareTo(b.Id);
}
