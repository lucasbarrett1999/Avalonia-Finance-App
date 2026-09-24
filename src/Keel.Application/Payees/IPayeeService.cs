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
}

/// <summary>A payee.</summary>
public sealed record PayeeDto(Guid Id, string Name, Guid? DefaultCategoryId);

/// <summary>Pre-fill values for a known payee.</summary>
public sealed record PayeeSuggestion(Guid PayeeId, string Name, Guid? CategoryId, string? CategoryName, string? Memo);
