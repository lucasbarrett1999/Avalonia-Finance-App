namespace Keel.Application.Tags;

/// <summary>
/// Transaction tags (F-TXN-8): the list for pickers and Settings → Tags, and tag management. Every change
/// is one audited, undoable ledger action that publishes <see cref="Messaging.LedgerChanged"/>; rules that
/// name a renamed or merged tag follow it (<see cref="Rules.RulesChanged"/>). Tags on a transaction are set
/// with <see cref="Ledger.SaveTransactionRequest.Tags"/>, which creates new tags in the same action.
/// </summary>
public interface ITagService
{
    /// <summary>All tags by name.</summary>
    Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct);

    /// <summary>All tags by name with how many transactions and rules use them (Settings → Tags).</summary>
    Task<IReadOnlyList<TagUsage>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Renames a tag; every transaction keeps it and rules that name it follow. Refuses an empty name, a name
    /// another tag has (merge instead), and the reserved <c>Flagged</c> tag either way. Undoable.
    /// </summary>
    Task<TagDto> RenameAsync(Guid tagId, string name, CancellationToken ct);

    /// <summary>
    /// Merges <paramref name="sourceId"/> into <paramref name="targetId"/>: its transactions get the target
    /// tag, rules that name it name the target, a subscription designation moves to the target, and the
    /// source tag is removed. Undoable.
    /// </summary>
    Task<TagMergeResult> MergeAsync(Guid sourceId, Guid targetId, CancellationToken ct);

    /// <summary>Deletes a tag and removes it from every transaction (and from the subscription designations); returns how many transactions had it. Undoable.</summary>
    Task<int> DeleteAsync(Guid tagId, CancellationToken ct);
}

/// <summary>A tag.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name.</param>
public sealed record TagDto(Guid Id, string Name);

/// <summary>A tag in Settings → Tags.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name.</param>
/// <param name="TransactionCount">Non-deleted transactions with the tag.</param>
/// <param name="RuleCount">Rules whose conditions or actions name the tag.</param>
/// <param name="IsReserved">The reserved <c>Flagged</c> tag (cannot be renamed or merged).</param>
/// <param name="IsSubscriptionTag">Marks subscriptions (Settings → Bills and subscriptions).</param>
public sealed record TagUsage(Guid Id, string Name, int TransactionCount, int RuleCount, bool IsReserved, bool IsSubscriptionTag);

/// <summary>Outcome of <see cref="ITagService.MergeAsync"/>.</summary>
/// <param name="Target">The surviving tag.</param>
/// <param name="TransactionsMoved">Transactions that now carry the target tag (and did not before).</param>
/// <param name="RulesUpdated">Rules rewritten to name the target.</param>
public sealed record TagMergeResult(TagDto Target, int TransactionsMoved, int RulesUpdated);
