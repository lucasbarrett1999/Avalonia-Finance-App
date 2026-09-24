using Keel.Domain;

namespace Keel.Application.Scheduling;

/// <summary>
/// Scheduled transactions (F-ACC-6): templates with an RFC 5545-subset rule
/// (<see cref="Keel.Domain.Scheduling.RecurrenceRule"/>, ADR 0030) whose instances show ghosted in
/// the register and are entered on their date (automatically or after a prompt). Entered instances
/// become transactions with <c>Source = Scheduled</c> and <c>ScheduledFromId</c>. Implemented in a
/// later M5 task.
/// </summary>
public interface IScheduledTransactionService
{
    /// <summary>Lists scheduled transactions, optionally of one account.</summary>
    Task<IReadOnlyList<ScheduledTransactionDto>> GetAsync(Guid? accountId, CancellationToken ct);

    /// <summary>Creates a scheduled transaction; the rule is validated.</summary>
    Task<ScheduledTransactionDto> CreateAsync(ScheduledTransactionEdit edit, CancellationToken ct);

    /// <summary>Edits a scheduled transaction.</summary>
    Task<ScheduledTransactionDto> UpdateAsync(Guid id, ScheduledTransactionEdit edit, CancellationToken ct);

    /// <summary>Deletes a scheduled transaction (entered instances stay).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Upcoming instances in a range, for ghost rows in the register and the Bills calendar.</summary>
    Task<IReadOnlyList<ScheduledInstanceDto>> GetUpcomingAsync(DateOnly from, DateOnly to, Guid? accountId, CancellationToken ct);

    /// <summary>
    /// Enters every auto-enter instance due on or before <paramref name="today"/> and returns the
    /// instances that need a prompt.
    /// </summary>
    Task<EnterDueResult> EnterDueAsync(DateOnly today, CancellationToken ct);

    /// <summary>Enters one instance now (from a prompt or a ghost row).</summary>
    Task<Guid> EnterInstanceAsync(Guid scheduledId, DateOnly instanceDate, CancellationToken ct);

    /// <summary>Skips one instance (advances the next date without entering it).</summary>
    Task SkipInstanceAsync(Guid scheduledId, DateOnly instanceDate, CancellationToken ct);

    /// <summary>Validates a rule for the editor: description and the next few dates, or the parse error.</summary>
    RecurrenceRuleCheck CheckRule(string rule, DateOnly start, int previewCount);

    /// <summary>One scheduled transaction, or null.</summary>
    Task<ScheduledTransactionDto?> GetByIdAsync(Guid id, CancellationToken ct);
}

/// <summary>A scheduled transaction.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="AccountId">Account.</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="PayeeName">Payee display name.</param>
/// <param name="Amount">Amount.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="TransferAccountId">Transfer target.</param>
/// <param name="Memo">Memo.</param>
/// <param name="Rule">Canonical rule text.</param>
/// <param name="Schedule">Rule description.</param>
/// <param name="NextDate">Next instance.</param>
/// <param name="EndDate">Last possible instance.</param>
/// <param name="AutoEnter">Entered automatically on its date.</param>
/// <param name="AccountName">Account display name.</param>
/// <param name="CategoryName">Category display name.</param>
/// <param name="TransferAccountName">Transfer target display name.</param>
/// <param name="IsFinished">Past its end date: no more instances.</param>
public sealed record ScheduledTransactionDto(
    Guid Id,
    Guid AccountId,
    Guid PayeeId,
    string PayeeName,
    Money Amount,
    Guid? CategoryId,
    Guid? TransferAccountId,
    string? Memo,
    string Rule,
    string Schedule,
    DateOnly NextDate,
    DateOnly? EndDate,
    bool AutoEnter,
    string? AccountName = null,
    string? CategoryName = null,
    string? TransferAccountName = null,
    bool IsFinished = false);

/// <summary>Input for creating or editing a scheduled transaction.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="Amount">Amount in minor units.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="TransferAccountId">Transfer target.</param>
/// <param name="Memo">Memo.</param>
/// <param name="Rule">Rule text (parsed and stored canonically).</param>
/// <param name="StartDate">First instance on or after this date.</param>
/// <param name="EndDate">Last possible instance.</param>
/// <param name="AutoEnter">Enter automatically.</param>
/// <param name="RecurringItemId">The recurring item this schedule is created from (linked on create).</param>
public sealed record ScheduledTransactionEdit(
    Guid AccountId,
    Guid PayeeId,
    long Amount,
    Guid? CategoryId,
    Guid? TransferAccountId,
    string? Memo,
    string Rule,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool AutoEnter,
    Guid? RecurringItemId = null);

/// <summary>One upcoming instance (a ghost row).</summary>
/// <param name="ScheduledId">Scheduled transaction.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Date">Instance date.</param>
/// <param name="Amount">Amount.</param>
/// <param name="PayeeName">Payee display name.</param>
/// <param name="IsOverdue">Due before today and not entered.</param>
/// <param name="IsNext">The schedule's next instance: the only one that can be entered or skipped.</param>
/// <param name="TransferAccountName">The other account of a transfer (for the "Transfer: account" payee text).</param>
/// <param name="CategoryName">Category.</param>
/// <param name="Memo">Memo.</param>
/// <param name="AutoEnter">Entered automatically on its date.</param>
public sealed record ScheduledInstanceDto(
    Guid ScheduledId,
    Guid AccountId,
    DateOnly Date,
    Money Amount,
    string PayeeName,
    bool IsOverdue,
    bool IsNext = false,
    string? TransferAccountName = null,
    string? CategoryName = null,
    string? Memo = null,
    bool AutoEnter = false);

/// <summary>Result of <see cref="IScheduledTransactionService.EnterDueAsync"/>.</summary>
/// <param name="EnteredTransactionIds">Transactions created.</param>
/// <param name="NeedsPrompt">Due instances of non-auto-enter schedules.</param>
public sealed record EnterDueResult(IReadOnlyList<Guid> EnteredTransactionIds, IReadOnlyList<ScheduledInstanceDto> NeedsPrompt);

/// <summary>Rule validation for the editor.</summary>
/// <param name="IsValid">Parsed.</param>
/// <param name="Error">Parse error naming the offending part.</param>
/// <param name="CanonicalRule">Canonical form.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="NextDates">The next instances from the start date.</param>
public sealed record RecurrenceRuleCheck(bool IsValid, string? Error, string? CanonicalRule, string? Description, IReadOnlyList<DateOnly> NextDates);
