using Keel.Domain.Budgeting;
using Keel.Domain.Recurring;
using Keel.Domain.Scheduling;

namespace Keel.Domain.Forecast;

/// <summary>
/// Cash-flow forecast (F-REP-4): daily balances of every open, on-budget cash account from today
/// through today + N days, from the cleared balance, scheduled-transaction occurrences, confirmed
/// recurring items and, optionally, the average daily discretionary spend. Pure; every day can be
/// explained. Definitions are in <c>docs/decisions/0033-cash-flow-forecast-definitions.md</c>.
/// </summary>
public static class ForecastEngine
{
    /// <summary>Name of the combined series.</summary>
    public const string CombinedName = "All cash accounts";

    /// <summary>Computes the forecast.</summary>
    public static ForecastResult Compute(ForecastInput input, ForecastOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= ForecastOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.Days);
        ArgumentOutOfRangeException.ThrowIfNegative(options.OverdueWindowDays);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DiscretionaryLookbackDays, 1);

        var start = input.Today;
        var end = start.AddDays(options.Days);
        var accounts = input.Accounts.Where(a => a.IsProjected).DistinctBy(a => a.Id).ToList();
        var accountIndex = accounts.Select((a, i) => (a.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var entries = new List<ForecastEntry>();
        var skipped = new List<ForecastSkip>();

        AddScheduled(input.Scheduled, accountIndex, start, end, options, entries, skipped);
        AddRecurring(input, accountIndex, start, end, options, entries, skipped);

        var discretionary = new List<DiscretionarySpend>();
        if (options.IncludeDiscretionarySpend)
        {
            foreach (var account in accounts)
            {
                var spend = ComputeDiscretionary(account, input, options.DiscretionaryLookbackDays);
                discretionary.Add(spend);
                if (spend.Daily == 0)
                {
                    continue;
                }

                for (var d = start.AddDays(1); d <= end; d = d.AddDays(1))
                {
                    entries.Add(new ForecastEntry(d, account.Id, ForecastEntryKind.Discretionary, -spend.Daily, null, null, "Average daily spending", false, d));
                }
            }
        }

        entries.Sort((a, b) =>
        {
            var byDate = a.Date.CompareTo(b.Date);
            if (byDate != 0)
            {
                return byDate;
            }

            var byAccount = accountIndex[a.AccountId].CompareTo(accountIndex[b.AccountId]);
            if (byAccount != 0)
            {
                return byAccount;
            }

            var byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0)
            {
                return byKind;
            }

            var byDue = a.DueDate.CompareTo(b.DueDate);
            return byDue != 0 ? byDue : Nullable.Compare(a.SourceId, b.SourceId);
        });

        var dayCount = options.Days + 1;
        var changes = new long[accounts.Count, dayCount];
        foreach (var e in entries)
        {
            changes[accountIndex[e.AccountId], e.Date.DayNumber - start.DayNumber] += e.Amount;
        }

        var series = new List<ForecastSeries>(accounts.Count);
        for (var a = 0; a < accounts.Count; a++)
        {
            var account = accounts[a];
            var daily = discretionary.FirstOrDefault(s => s.AccountId == account.Id)?.Daily ?? 0;
            series.Add(BuildSeries(account.Id, account.Name, account.ClearedBalance, daily, start, dayCount, i => changes[a, i], options.Floor));
        }

        var combined = BuildSeries(
            null,
            CombinedName,
            accounts.Sum(a => a.ClearedBalance),
            discretionary.Sum(s => s.Daily),
            start,
            dayCount,
            i =>
            {
                long sum = 0;
                for (var a = 0; a < accounts.Count; a++)
                {
                    sum += changes[a, i];
                }

                return sum;
            },
            options.Floor);

        return new ForecastResult(start, end, options.Floor, series, combined, entries, skipped, discretionary);
    }

    /// <summary>
    /// The average daily discretionary spend of an account (ADR 0033): outflows in the look-back window
    /// ending yesterday, excluding transfers, rows entered from scheduled transactions, and rows whose
    /// payee has a recurring item (any status but Dismissed) or a scheduled transaction on the same
    /// account; divided by the days in the window (fewer when the account opened inside it).
    /// </summary>
    public static DiscretionarySpend ComputeDiscretionary(ForecastAccount account, ForecastInput input, int lookbackDays = 90)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackDays, 1);

        var to = input.Today.AddDays(-1);
        var from = input.Today.AddDays(-lookbackDays);
        if (account.OpeningDate is { } opened && opened > from)
        {
            from = opened <= to ? opened : to;
        }

        var days = Math.Max(1, to.DayNumber - from.DayNumber + 1);
        var recurringPayees = new HashSet<Guid>();
        foreach (var r in input.Recurring)
        {
            if (r.Status != RecurringStatus.Dismissed && (r.AccountId is null || r.AccountId == account.Id))
            {
                recurringPayees.Add(r.PayeeId);
            }
        }

        foreach (var s in input.Scheduled)
        {
            if (s.AccountId == account.Id || s.TransferAccountId == account.Id)
            {
                recurringPayees.Add(s.PayeeId);
            }
        }

        long total = 0;
        int counted = 0, excluded = 0;
        foreach (var t in input.History)
        {
            if (t.AccountId != account.Id || t.Date < from || t.Date > to || t.Amount >= 0)
            {
                continue;
            }

            if (t.IsTransfer || t.ScheduledFromId is not null || (t.PayeeId is { } payee && recurringPayees.Contains(payee)))
            {
                excluded++;
                continue;
            }

            total -= t.Amount;
            counted++;
        }

        return new DiscretionarySpend(account.Id, from, to, days, total, counted, excluded, QuickAssign.DivideHalfEven(total, days));
    }

    private static void AddScheduled(
        IReadOnlyList<ForecastScheduled> scheduled,
        Dictionary<Guid, int> accountIndex,
        DateOnly start,
        DateOnly end,
        ForecastOptions options,
        List<ForecastEntry> entries,
        List<ForecastSkip> skipped)
    {
        foreach (var s in scheduled)
        {
            var own = accountIndex.ContainsKey(s.AccountId);
            var other = s.TransferAccountId is { } t && t != s.AccountId && accountIndex.ContainsKey(t);
            if (!own && !other)
            {
                skipped.Add(new ForecastSkip(s.Id, ForecastSourceKind.Scheduled, ForecastSkipReason.NotCashAccount, s.Label));
                continue;
            }

            var last = s.EndDate is { } e && e < end ? e : end;
            foreach (var (date, due, overdue) in Place(s.Rule, s.RuleStart ?? s.NextDate, s.NextDate, start, last, options.OverdueWindowDays))
            {
                if (own)
                {
                    entries.Add(new ForecastEntry(date, s.AccountId, ForecastEntryKind.Scheduled, s.Amount, s.Id, s.PayeeId, s.Label, overdue, due, s.TransferAccountId));
                }

                if (other)
                {
                    entries.Add(new ForecastEntry(date, s.TransferAccountId!.Value, ForecastEntryKind.ScheduledTransfer, checked(-s.Amount), s.Id, s.PayeeId, s.Label, overdue, due, s.AccountId));
                }
            }
        }
    }

    private static void AddRecurring(
        ForecastInput input,
        Dictionary<Guid, int> accountIndex,
        DateOnly start,
        DateOnly end,
        ForecastOptions options,
        List<ForecastEntry> entries,
        List<ForecastSkip> skipped)
    {
        var scheduledIds = input.Scheduled.Select(s => s.Id).ToHashSet();
        foreach (var r in input.Recurring)
        {
            ForecastSkipReason? reason = null;
            if (r.Status != RecurringStatus.Active)
            {
                reason = ForecastSkipReason.NotConfirmed;
            }
            else if (r.AccountId is not { } accountId)
            {
                reason = ForecastSkipReason.NoAccount;
            }
            else if (!accountIndex.ContainsKey(accountId))
            {
                reason = ForecastSkipReason.NotCashAccount;
            }
            else if ((r.ScheduledTransactionId is { } linked && scheduledIds.Contains(linked))
                || input.Scheduled.Any(s => s.PayeeId == r.PayeeId && (s.AccountId == accountId || s.TransferAccountId == accountId)))
            {
                reason = ForecastSkipReason.CoveredBySchedule;
            }

            if (reason is { } why)
            {
                skipped.Add(new ForecastSkip(r.Id, ForecastSourceKind.Recurring, why, r.Label));
                continue;
            }

            var rule = r.Rule ?? RecurringSchedule.InferRule(r.Cadence, r.NextExpectedDate, r.LastSeenDate);
            foreach (var (date, due, overdue) in Place(rule, r.NextExpectedDate, r.NextExpectedDate, start, end, options.OverdueWindowDays))
            {
                entries.Add(new ForecastEntry(date, r.AccountId!.Value, ForecastEntryKind.Recurring, r.ExpectedAmount, r.Id, r.PayeeId, r.Label, overdue, due));
            }
        }
    }

    // Occurrences from `next` through `last`; those before `start` (not yet entered) land on `start`
    // when they are at most `overdueWindow` days old.
    private static IEnumerable<(DateOnly Date, DateOnly Due, bool Overdue)> Place(
        RecurrenceRule rule,
        DateOnly ruleStart,
        DateOnly next,
        DateOnly start,
        DateOnly last,
        int overdueWindow)
    {
        foreach (var due in rule.Occurrences(ruleStart, next, last))
        {
            if (due >= start)
            {
                yield return (due, due, false);
            }
            else if (start.DayNumber - due.DayNumber <= overdueWindow)
            {
                yield return (start, due, true);
            }
        }
    }

    private static ForecastSeries BuildSeries(
        Guid? accountId,
        string name,
        long startingBalance,
        long daily,
        DateOnly start,
        int dayCount,
        Func<int, long> change,
        long? floor)
    {
        var days = new List<ForecastDay>(dayCount);
        var balance = startingBalance;
        ForecastDay? lowest = null;
        var below = new List<ForecastDay>();
        for (var i = 0; i < dayCount; i++)
        {
            var delta = change(i);
            var day = new ForecastDay(start.AddDays(i), balance, delta, checked(balance + delta));
            balance = day.Balance;
            days.Add(day);
            if (lowest is null || day.Balance < lowest.Balance)
            {
                lowest = day;
            }

            if (floor is { } f && day.Balance < f)
            {
                below.Add(day);
            }
        }

        return new ForecastSeries(accountId, name, startingBalance, daily, days, lowest!, below);
    }
}
