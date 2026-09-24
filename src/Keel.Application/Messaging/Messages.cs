namespace Keel.Application.Messaging;

/// <summary>Publishes change notifications to open view models (PRD 7.3).</summary>
public interface IMessageBus
{
    /// <summary>Sends a message to every current recipient of <typeparamref name="TMessage"/>.</summary>
    void Publish<TMessage>(TMessage message)
        where TMessage : class;
}

/// <summary>Transactions changed in these accounts and months; ledger views refresh incrementally.</summary>
/// <param name="AccountIds">Affected accounts.</param>
/// <param name="MonthsAffected">Affected months (first-of-month dates).</param>
public sealed record LedgerChanged(IReadOnlyCollection<Guid> AccountIds, IReadOnlyCollection<DateOnly> MonthsAffected);

/// <summary>Budget numbers changed for these months (and, by carry-forward, later ones).</summary>
/// <param name="Months">Affected months (first-of-month dates).</param>
public sealed record BudgetChanged(IReadOnlyCollection<DateOnly> Months);
