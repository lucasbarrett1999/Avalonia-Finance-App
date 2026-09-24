using Keel.Domain;
using Keel.Domain.Alerts;

namespace Keel.Application.Alerts;

/// <summary>
/// The notification center (F-REC-3, PRD 9.1 bell). Alerts are created by
/// <see cref="AlertEvaluator"/> (ADR 0032) after imports, detection runs and once a day, stored
/// with their idempotency key, and never leave the machine. Publishes <see cref="AlertsChanged"/>.
/// Implemented in a later M5 task.
/// </summary>
public interface IAlertService
{
    /// <summary>Lists alerts, newest first.</summary>
    Task<IReadOnlyList<AlertDto>> GetAlertsAsync(bool includeDismissed, CancellationToken ct);

    /// <summary>Unread, undismissed alerts (the bell badge).</summary>
    Task<int> GetUnreadCountAsync(CancellationToken ct);

    /// <summary>Marks alerts read.</summary>
    Task MarkReadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Dismisses an alert; it is never proposed again (its key stays stored).</summary>
    Task DismissAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Evaluates alerts for a batch of new transactions (may be empty) and the decisions of a detection
    /// run, as of <paramref name="today"/>, and stores the new ones.
    /// </summary>
    Task<IReadOnlyList<AlertDto>> EvaluateAsync(AlertEvaluationRequest request, CancellationToken ct);
}

/// <summary>What to evaluate.</summary>
/// <param name="Today">Evaluation date.</param>
/// <param name="NewTransactionIds">Transactions just imported or entered.</param>
/// <param name="CreatedRecurringItemIds">Items a detection run just created (new-item alerts).</param>
public sealed record AlertEvaluationRequest(DateOnly Today, IReadOnlyCollection<Guid> NewTransactionIds, IReadOnlyCollection<Guid> CreatedRecurringItemIds);

/// <summary>An alert for the notification center.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Kind">Kind.</param>
/// <param name="RecurringItemId">Related item.</param>
/// <param name="TransactionId">Triggering transaction.</param>
/// <param name="PayeeName">Payee display name.</param>
/// <param name="Amount">Charge or expected amount.</param>
/// <param name="PreviousAmount">Previous amount (price increase) or trial amount.</param>
/// <param name="IncreasePercent">Price increase in percent.</param>
/// <param name="ExpectedDate">Missing item: expected date.</param>
/// <param name="DaysLate">Missing item: days late.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="ReadAt">Read time (UTC).</param>
/// <param name="DismissedAt">Dismissal time (UTC).</param>
public sealed record AlertDto(
    Guid Id,
    AlertKind Kind,
    Guid? RecurringItemId,
    Guid? TransactionId,
    string? PayeeName,
    Money? Amount,
    Money? PreviousAmount,
    decimal? IncreasePercent,
    DateOnly? ExpectedDate,
    int? DaysLate,
    DateTime CreatedAt,
    DateTime? ReadAt,
    DateTime? DismissedAt);

/// <summary>Alerts were added, read or dismissed; the bell badge refreshes.</summary>
/// <param name="UnreadCount">Unread, undismissed alerts after the change.</param>
public sealed record AlertsChanged(int UnreadCount);
