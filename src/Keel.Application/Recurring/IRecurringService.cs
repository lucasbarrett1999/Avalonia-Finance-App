using Keel.Domain;
using Keel.Domain.Recurring;

namespace Keel.Application.Recurring;

/// <summary>
/// Recurring items and the Bills screen (F-REC-1, F-REC-2, F-REC-4, PRD 9.6). Detection runs
/// <see cref="RecurringDetector"/> and <see cref="RecurringReconciler"/> over the ledger and applies
/// the decisions; alerts for the run go through <see cref="Alerts.IAlertService"/>. Every mutation
/// records an <c>AuditEvent</c> and publishes <see cref="RecurringChanged"/>. Implemented in a later
/// M5 task.
/// </summary>
public interface IRecurringService
{
    /// <summary>Runs detection as of <paramref name="asOf"/> (nightly and after every import).</summary>
    Task<RecurringDetectionSummary> DetectAsync(DateOnly asOf, CancellationToken ct);

    /// <summary>Lists items for the Bills list, calendar and subscriptions tabs.</summary>
    Task<IReadOnlyList<RecurringItemDto>> GetItemsAsync(RecurringItemFilter filter, CancellationToken ct);

    /// <summary>One item with its history, or null.</summary>
    Task<RecurringItemDetailDto?> GetItemAsync(Guid id, CancellationToken ct);

    /// <summary>Monthly recurring outflow and inflow, subscription and bill totals (F-REC-2).</summary>
    Task<RecurringTotalsDto> GetTotalsAsync(bool includeUnconfirmed, CancellationToken ct);

    /// <summary>Upcoming occurrences of items in a date range (calendar, "upcoming bills" card).</summary>
    Task<IReadOnlyList<RecurringOccurrenceDto>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Confirms a detected item (Detected → Active).</summary>
    Task ConfirmAsync(Guid id, CancellationToken ct);

    /// <summary>Pauses an item (it stays, but is not forecast or alerted).</summary>
    Task PauseAsync(Guid id, CancellationToken ct);

    /// <summary>Resumes a paused or ended item (→ Active).</summary>
    Task ResumeAsync(Guid id, CancellationToken ct);

    /// <summary>Dismisses an item; detection will not recreate it (6.6 step 7).</summary>
    Task DismissAsync(Guid id, CancellationToken ct);

    /// <summary>Re-enables detection for a payee whose item was dismissed.</summary>
    Task ReenableDetectionAsync(Guid payeeId, CancellationToken ct);

    /// <summary>Creates an item by hand (F-REC-1).</summary>
    Task<RecurringItemDto> CreateAsync(RecurringItemEdit item, CancellationToken ct);

    /// <summary>Edits an item's cadence, amount, next date, category, account and subscription flag.</summary>
    Task<RecurringItemDto> UpdateAsync(Guid id, RecurringItemEdit item, CancellationToken ct);

    /// <summary>
    /// F-REC-4: creates or replaces a monthly set-aside target on the item's category equal to its
    /// monthly set-aside (<see cref="RecurringMath.MonthlySetAside"/>).
    /// </summary>
    Task CreateTargetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Runs detection after an import (F-REC-1 "on import") and evaluates alerts for the imported
    /// transactions (F-REC-3).
    /// </summary>
    Task<RecurringDetectionSummary> DetectAfterImportAsync(DateOnly asOf, IReadOnlyCollection<Guid> newTransactionIds, CancellationToken ct);

    /// <summary>
    /// The once-per-app-day run (F-REC-1 "nightly"): detection and alerts when detection has not run on
    /// <paramref name="today"/> yet; null when it already ran today.
    /// </summary>
    Task<RecurringDetectionSummary?> RunDailyAsync(DateOnly today, CancellationToken ct);

    /// <summary>The date of the last detection run, or null before the first (the Bills empty state).</summary>
    Task<DateOnly?> GetLastDetectionDateAsync(CancellationToken ct);

    /// <summary>The user's subscription designations (F-REC-2): category groups and tags.</summary>
    Task<SubscriptionDesignations> GetSubscriptionDesignationsAsync(CancellationToken ct);

    /// <summary>Saves the subscription designations and reclassifies every item with them.</summary>
    Task SetSubscriptionDesignationsAsync(SubscriptionDesignations designations, CancellationToken ct);

    /// <summary>
    /// Finds transactions imported (file or provider) since the last call, from the audit log, and runs
    /// <see cref="DetectAfterImportAsync"/> for them; null when there were none. The first call only
    /// sets the starting point. The desktop coordinator calls this after every <c>LedgerChanged</c>.
    /// </summary>
    Task<RecurringDetectionSummary?> DetectNewImportsAsync(DateOnly today, CancellationToken ct);

    /// <summary>Links (or unlinks, with null) a scheduled transaction created from an item.</summary>
    Task LinkScheduledAsync(Guid id, Guid? scheduledTransactionId, CancellationToken ct);
}

/// <summary>Which items to list.</summary>
/// <param name="Statuses">Statuses to include; empty for all but Dismissed.</param>
/// <param name="Kind">Subscriptions, bills, income or all.</param>
/// <param name="AccountId">Only items of this account.</param>
public sealed record RecurringItemFilter(
    IReadOnlyCollection<RecurringStatus> Statuses,
    RecurringItemKind Kind = RecurringItemKind.All,
    Guid? AccountId = null);

/// <summary>Bills-screen grouping of items.</summary>
public enum RecurringItemKind
{
    /// <summary>Every item.</summary>
    All,

    /// <summary>Outflows classified as subscriptions.</summary>
    Subscriptions,

    /// <summary>Outflows that are not subscriptions.</summary>
    Bills,

    /// <summary>Inflows (paychecks).</summary>
    Income,
}

/// <summary>A recurring item as the Bills screen shows it.</summary>
/// <param name="Id">Item id.</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="PayeeName">Payee display name.</param>
/// <param name="AccountId">Account.</param>
/// <param name="AccountName">Account display name.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="CategoryName">Category display name.</param>
/// <param name="Cadence">Cadence.</param>
/// <param name="Schedule">Rule description ("Every month on the 1st").</param>
/// <param name="ExpectedAmount">Expected amount per occurrence.</param>
/// <param name="AmountTolerance">Tolerance.</param>
/// <param name="IsVariableAmount">Variable amount (utilities).</param>
/// <param name="MonthlyEquivalent">Monthly equivalent (F-REC-2).</param>
/// <param name="NextExpectedDate">Next expected date.</param>
/// <param name="LastSeenDate">Last occurrence.</param>
/// <param name="Confidence">Detection confidence in [0, 1].</param>
/// <param name="Status">Status.</param>
/// <param name="IsSubscription">Subscription rather than bill.</param>
/// <param name="ScheduledTransactionId">Linked scheduled transaction.</param>
public sealed record RecurringItemDto(
    Guid Id,
    Guid PayeeId,
    string PayeeName,
    Guid? AccountId,
    string? AccountName,
    Guid? CategoryId,
    string? CategoryName,
    RecurrenceCadence Cadence,
    string Schedule,
    Money ExpectedAmount,
    Money AmountTolerance,
    bool IsVariableAmount,
    Money MonthlyEquivalent,
    DateOnly NextExpectedDate,
    DateOnly LastSeenDate,
    double Confidence,
    RecurringStatus Status,
    bool IsSubscription,
    Guid? ScheduledTransactionId);

/// <summary>An item with its occurrences and the detection explanation (Bills detail panel).</summary>
/// <param name="Item">The item.</param>
/// <param name="History">Past occurrences, oldest first (amount history chart, price changes).</param>
/// <param name="Scores">Per-cadence gap statistics of the latest detection, when detected.</param>
public sealed record RecurringItemDetailDto(
    RecurringItemDto Item,
    IReadOnlyList<RecurringOccurrenceDto> History,
    IReadOnlyList<CadenceScore> Scores);

/// <summary>One past or expected occurrence.</summary>
/// <param name="ItemId">Item.</param>
/// <param name="Date">Date.</param>
/// <param name="Amount">Actual (past) or expected amount.</param>
/// <param name="TransactionId">The ledger transaction, for past occurrences.</param>
/// <param name="IsExpected">True for a projected occurrence.</param>
public sealed record RecurringOccurrenceDto(Guid ItemId, DateOnly Date, Money Amount, Guid? TransactionId, bool IsExpected);

/// <summary>Bills totals (F-REC-2); outflow totals are negative.</summary>
/// <param name="MonthlyOutflow">Monthly recurring outflow.</param>
/// <param name="MonthlyInflow">Monthly recurring inflow.</param>
/// <param name="SubscriptionsMonthly">Subscriptions per month.</param>
/// <param name="SubscriptionsYearly">Subscriptions per year.</param>
/// <param name="BillsMonthly">Bills per month.</param>
/// <param name="BillsYearly">Bills per year.</param>
/// <param name="ItemCount">Items counted.</param>
public sealed record RecurringTotalsDto(
    Money MonthlyOutflow,
    Money MonthlyInflow,
    Money SubscriptionsMonthly,
    Money SubscriptionsYearly,
    Money BillsMonthly,
    Money BillsYearly,
    int ItemCount);

/// <summary>Input for creating or editing an item by hand.</summary>
/// <param name="PayeeId">Payee.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Cadence">Cadence.</param>
/// <param name="ExpectedAmount">Expected amount (minor units, signed).</param>
/// <param name="NextExpectedDate">Next expected date.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="IsSubscription">Subscription flag.</param>
public sealed record RecurringItemEdit(
    Guid PayeeId,
    Guid? AccountId,
    RecurrenceCadence Cadence,
    long ExpectedAmount,
    DateOnly NextExpectedDate,
    Guid? CategoryId,
    bool IsSubscription);

/// <summary>Result of a detection run.</summary>
/// <param name="Created">New items (status Detected).</param>
/// <param name="Updated">Items whose detection fields changed.</param>
/// <param name="Ended">Items ended because their pattern lapsed.</param>
/// <param name="Unchanged">Items already up to date.</param>
/// <param name="SkippedDismissed">Detections that matched a dismissed item.</param>
/// <param name="AlertsCreated">Alerts created by the run.</param>
public sealed record RecurringDetectionSummary(int Created, int Updated, int Ended, int Unchanged, int SkippedDismissed, int AlertsCreated);

/// <summary>Recurring items changed; the Bills screen and forecast refresh.</summary>
/// <param name="ItemIds">Affected items.</param>
public sealed record RecurringChanged(IReadOnlyCollection<Guid> ItemIds);

/// <summary>Which category groups and tags mark an item as a subscription (F-REC-2); a data-file setting.</summary>
/// <param name="GroupIds">Designated category groups.</param>
/// <param name="TagIds">Designated tags.</param>
public sealed record SubscriptionDesignations(IReadOnlyCollection<Guid> GroupIds, IReadOnlyCollection<Guid> TagIds)
{
    /// <summary>No designations.</summary>
    public static SubscriptionDesignations None { get; } = new([], []);
}
