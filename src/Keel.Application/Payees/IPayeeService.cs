namespace Keel.Application.Payees;

/// <summary>Payees: get-or-create by normalized name, autocomplete, and last-used defaults (PRD 9.4).</summary>
public interface IPayeeService
{
    /// <summary>Returns the payee with this normalized name, creating it if needed.</summary>
    Task<PayeeDto> GetOrCreateAsync(string name, CancellationToken ct);

    /// <summary>Payees whose name starts with (then contains) <paramref name="text"/>, most used first.</summary>
    Task<IReadOnlyList<PayeeDto>> SearchAsync(string text, int limit, CancellationToken ct);

    /// <summary>
    /// What to pre-fill when a known payee is chosen: the category and memo of its most recent
    /// transaction (preferring <paramref name="accountId"/>), else the payee's default category.
    /// Null when the payee is unknown.
    /// </summary>
    Task<PayeeSuggestion?> GetSuggestionAsync(string name, Guid? accountId, CancellationToken ct);

    /// <summary>Payees for Settings → Payees (F-TXN-9): name contains <paramref name="search"/>, alphabetical, with use counts.</summary>
    Task<IReadOnlyList<PayeeListItem>> ListAsync(string? search, int limit, CancellationToken ct);

    /// <summary>Sets (or clears) a payee's default category, used by categorization at 0.95 confidence. Undoable.</summary>
    Task SetDefaultCategoryAsync(Guid payeeId, Guid? categoryId, CancellationToken ct);

    /// <summary>
    /// Renames a payee; every transaction of the payee shows the new name (retroactive). When
    /// another payee already has that name, this payee's transactions move to it. Undoable.
    /// </summary>
    Task<PayeeDto> RenameAsync(Guid payeeId, string name, CancellationToken ct);

    /// <summary>What merging <paramref name="payeeIds"/> into <paramref name="survivorId"/> would change (the confirmation's counts).</summary>
    Task<PayeeMergePreview> PreviewMergeAsync(IReadOnlyCollection<Guid> payeeIds, Guid survivorId, CancellationToken ct);

    /// <summary>
    /// Merges payees into <paramref name="survivorId"/> (F-TXN-9, ADR 0097) in one undoable action: every
    /// transaction (deleted ones too), scheduled transaction and recurring item of the merged payees moves to the
    /// survivor, rules that set or equal a merged payee's name name the survivor, the survivor keeps its default
    /// category or takes the first merged payee's, and the merged payees are removed. <paramref name="payeeIds"/>
    /// may include the survivor. Throws <see cref="Ledger.LedgerValidationException"/> when nothing would merge.
    /// </summary>
    Task<PayeeMergeResult> MergeAsync(IReadOnlyCollection<Guid> payeeIds, Guid survivorId, CancellationToken ct);
}

/// <summary>Counts shown before a payee merge.</summary>
/// <param name="Survivor">The payee that stays.</param>
/// <param name="Merged">The payees that go, in list order.</param>
/// <param name="Transactions">Transactions that move to the survivor.</param>
/// <param name="ScheduledTransactions">Scheduled transactions that move.</param>
/// <param name="RecurringItems">Recurring items that move.</param>
/// <param name="Rules">Rules rewritten to name the survivor.</param>
/// <param name="DefaultCategoryId">The survivor's default category afterwards.</param>
public sealed record PayeeMergePreview(
    PayeeDto Survivor,
    IReadOnlyList<PayeeDto> Merged,
    int Transactions,
    int ScheduledTransactions,
    int RecurringItems,
    int Rules,
    Guid? DefaultCategoryId);

/// <summary>Outcome of a payee merge.</summary>
/// <param name="Survivor">The surviving payee after the merge.</param>
/// <param name="MergedCount">Payees removed.</param>
/// <param name="Transactions">Transactions moved.</param>
/// <param name="ScheduledTransactions">Scheduled transactions moved.</param>
/// <param name="RecurringItems">Recurring items moved.</param>
/// <param name="Rules">Rules rewritten.</param>
public sealed record PayeeMergeResult(PayeeDto Survivor, int MergedCount, int Transactions, int ScheduledTransactions, int RecurringItems, int Rules);

/// <summary>A payee.</summary>
public sealed record PayeeDto(Guid Id, string Name, Guid? DefaultCategoryId);

/// <summary>Pre-fill values for a known payee.</summary>
public sealed record PayeeSuggestion(Guid PayeeId, string Name, Guid? CategoryId, string? CategoryName, string? Memo);
