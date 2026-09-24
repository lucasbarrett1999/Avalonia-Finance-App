using Keel.Domain.Entities;
using Keel.Domain.Scheduling;

namespace Keel.Domain.Forecast;

/// <summary>An account as the forecast sees it. Only open, on-budget, cash-like accounts are projected.</summary>
/// <param name="Id">Account id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Account type.</param>
/// <param name="IsOnBudget">On-budget flag.</param>
/// <param name="ClearedBalance">Current cleared balance in minor units (the starting point, F-REP-4).</param>
/// <param name="IsClosed">Closed accounts are not projected.</param>
/// <param name="OpeningDate">Opening date; limits the discretionary-spend window for new accounts.</param>
public sealed record ForecastAccount(
    Guid Id,
    string Name,
    AccountType Type,
    bool IsOnBudget,
    long ClearedBalance,
    bool IsClosed = false,
    DateOnly? OpeningDate = null)
{
    /// <summary>True for an open, on-budget, cash-like account (checking, savings, cash).</summary>
    public bool IsProjected => IsOnBudget && !IsClosed && AccountTypeInfo.IsCashLike(Type);
}

/// <summary>A scheduled transaction with its parsed rule (F-ACC-6).</summary>
/// <param name="Id">Scheduled transaction id.</param>
/// <param name="AccountId">Account.</param>
/// <param name="PayeeId">Payee (the double-count guard compares payees).</param>
/// <param name="Amount">Signed amount in minor units.</param>
/// <param name="Rule">Recurrence rule.</param>
/// <param name="NextDate">Next instance not yet entered.</param>
/// <param name="EndDate">Last possible instance date.</param>
/// <param name="TransferAccountId">For a transfer: the other account, which gets the opposite amount.</param>
/// <param name="RuleStart">The rule's anchor (DTSTART); defaults to <paramref name="NextDate"/>.</param>
/// <param name="Label">Display label (payee name) for explanations.</param>
public sealed record ForecastScheduled(
    Guid Id,
    Guid AccountId,
    Guid PayeeId,
    long Amount,
    RecurrenceRule Rule,
    DateOnly NextDate,
    DateOnly? EndDate = null,
    Guid? TransferAccountId = null,
    DateOnly? RuleStart = null,
    string? Label = null)
{
    /// <summary>Projects a stored scheduled transaction (parses its rule).</summary>
    /// <exception cref="RecurrenceRuleFormatException">The stored rule is invalid.</exception>
    public static ForecastScheduled From(ScheduledTransaction entity, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new ForecastScheduled(
            entity.Id,
            entity.AccountId,
            entity.PayeeId,
            entity.Amount,
            RecurrenceRule.Parse(entity.RecurrenceRule),
            entity.NextDate,
            entity.EndDate,
            entity.TransferAccountId,
            null,
            label);
    }
}

/// <summary>A recurring item as the forecast sees it.</summary>
/// <param name="Id">Item id.</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="AccountId">Account (items without one cannot be placed).</param>
/// <param name="Cadence">Cadence.</param>
/// <param name="ExpectedAmount">Expected amount per occurrence.</param>
/// <param name="NextExpectedDate">Next expected date.</param>
/// <param name="LastSeenDate">Last occurrence.</param>
/// <param name="Status">Only Active (confirmed) items are projected.</param>
/// <param name="ScheduledTransactionId">A scheduled transaction created from this item.</param>
/// <param name="Rule">Projection rule; inferred from the dates when null (see <c>RecurringSchedule.InferRule</c>).</param>
/// <param name="Label">Display label for explanations.</param>
public sealed record ForecastRecurring(
    Guid Id,
    Guid PayeeId,
    Guid? AccountId,
    RecurrenceCadence Cadence,
    long ExpectedAmount,
    DateOnly NextExpectedDate,
    DateOnly LastSeenDate,
    RecurringStatus Status,
    Guid? ScheduledTransactionId = null,
    RecurrenceRule? Rule = null,
    string? Label = null)
{
    /// <summary>Projects a stored item.</summary>
    public static ForecastRecurring From(RecurringItem item, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ForecastRecurring(
            item.Id,
            item.PayeeId,
            item.AccountId,
            item.Cadence,
            item.ExpectedAmount,
            item.NextExpectedDate,
            item.LastSeenDate,
            item.Status,
            item.ScheduledTransactionId,
            null,
            label);
    }
}

/// <summary>A past transaction, for the average daily discretionary spend.</summary>
/// <param name="Id">Transaction id.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Date">Date.</param>
/// <param name="Amount">Signed amount (a split parent's amount; splits do not matter here).</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="IsTransfer">One side of a transfer (excluded).</param>
/// <param name="ScheduledFromId">Entered from a scheduled transaction (excluded).</param>
public sealed record ForecastHistoryTransaction(
    Guid Id,
    Guid AccountId,
    DateOnly Date,
    long Amount,
    Guid? PayeeId,
    bool IsTransfer,
    Guid? ScheduledFromId = null);

/// <summary>Everything the forecast needs (F-REP-4).</summary>
/// <param name="Today">Day 0 of the forecast.</param>
/// <param name="Accounts">Accounts (non-projected ones are ignored).</param>
/// <param name="Scheduled">Scheduled transactions.</param>
/// <param name="Recurring">Recurring items.</param>
/// <param name="History">Transactions of at least the look-back window, for discretionary spend.</param>
public sealed record ForecastInput(
    DateOnly Today,
    IReadOnlyList<ForecastAccount> Accounts,
    IReadOnlyList<ForecastScheduled> Scheduled,
    IReadOnlyList<ForecastRecurring> Recurring,
    IReadOnlyList<ForecastHistoryTransaction> History);

/// <summary>Forecast options.</summary>
/// <param name="Days">Days after today to project (default 90).</param>
/// <param name="IncludeDiscretionarySpend">Subtract the average daily discretionary spend every future day (the F-REP-4 toggle).</param>
/// <param name="Floor">User floor in minor units; days with a closing balance below it are listed.</param>
/// <param name="DiscretionaryLookbackDays">Window for the average (default 90).</param>
/// <param name="OverdueWindowDays">Occurrences up to this many days overdue land on day 0; older ones are ignored.</param>
public sealed record ForecastOptions(
    int Days = 90,
    bool IncludeDiscretionarySpend = false,
    long? Floor = null,
    int DiscretionaryLookbackDays = 90,
    int OverdueWindowDays = 14)
{
    /// <summary>Defaults: 90 days, no discretionary spend, no floor.</summary>
    public static ForecastOptions Default { get; } = new();
}
