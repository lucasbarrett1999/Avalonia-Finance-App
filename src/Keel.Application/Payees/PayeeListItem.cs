namespace Keel.Application.Payees;

/// <summary>A payee in Settings → Payees (F-TXN-9).</summary>
/// <param name="Id">Payee id.</param>
/// <param name="Name">Display name.</param>
/// <param name="DefaultCategoryId">Default category, if set.</param>
/// <param name="DefaultCategoryName">Its name.</param>
/// <param name="TransactionCount">Non-deleted transactions with this payee.</param>
public sealed record PayeeListItem(Guid Id, string Name, Guid? DefaultCategoryId, string? DefaultCategoryName, int TransactionCount);
