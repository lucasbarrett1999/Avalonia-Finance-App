using Keel.Domain.Entities;
using Keel.Domain.Import;

namespace Keel.Domain.Rules;

/// <summary>
/// An immutable, persistence-free view of one transaction: the input of the
/// <see cref="RuleEngine"/>, the <see cref="RuleSuggester"/> and the categorization learner.
/// Amounts are minor units with outflows negative (PRD 6.1).
/// </summary>
public sealed record TransactionSnapshot
{
    /// <summary>Transaction id (<see cref="Guid.Empty"/> for a row not yet inserted).</summary>
    public Guid Id { get; init; }

    /// <summary>Account the transaction belongs to.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Ledger date.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>Signed amount in minor units; outflows negative.</summary>
    public required long Amount { get; init; }

    /// <summary>ISO 4217 currency of <see cref="Amount"/>.</summary>
    public string Currency { get; init; } = Keel.Domain.Currency.Default;

    /// <summary>The descriptor exactly as imported or typed. Rules never change it.</summary>
    public string PayeeRaw { get; init; } = string.Empty;

    /// <summary>
    /// The current payee name (the resolved payee's name, or the raw descriptor when there is
    /// none). A "set payee" action changes it.
    /// </summary>
    public string Payee { get; init; } = string.Empty;

    /// <summary>Resolved payee, if any.</summary>
    public Guid? PayeeId { get; init; }

    /// <summary>Memo.</summary>
    public string? Memo { get; init; }

    /// <summary>Category; null for split parents, on-budget transfers and uncategorized rows.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>The other account of a transfer.</summary>
    public Guid? TransferAccountId { get; init; }

    /// <summary>Origin of the transaction.</summary>
    public TransactionSource Source { get; init; }

    /// <summary>Approved in the review queue.</summary>
    public bool IsApproved { get; init; }

    /// <summary>Flagged for attention by a rule or the user.</summary>
    public bool IsFlagged { get; init; }

    /// <summary>Tag names.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Splits; when present, <see cref="CategoryId"/> is null and the amounts sum to <see cref="Amount"/>.</summary>
    public IReadOnlyList<SnapshotSplit> Splits { get; init; } = [];

    /// <summary>The payee text used for matching and learning: <see cref="Payee"/>, or <see cref="PayeeRaw"/> when it is blank.</summary>
    public string EffectivePayee => string.IsNullOrWhiteSpace(Payee) ? PayeeRaw : Payee;

    /// <summary>Whether the transaction is split.</summary>
    public bool IsSplit => Splits.Count > 0;

    /// <summary>
    /// Creates a snapshot of a ledger transaction. <paramref name="payeeName"/> is the resolved
    /// payee's display name (null falls back to <see cref="Transaction.PayeeRaw"/>).
    /// </summary>
    public static TransactionSnapshot From(Transaction transaction, string? payeeName = null, IEnumerable<string>? tags = null, string currency = Keel.Domain.Currency.Default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new TransactionSnapshot
        {
            Id = transaction.Id,
            AccountId = transaction.AccountId,
            Date = transaction.Date,
            Amount = transaction.Amount,
            Currency = currency,
            PayeeRaw = transaction.PayeeRaw,
            Payee = payeeName ?? transaction.PayeeRaw,
            PayeeId = transaction.PayeeId,
            Memo = transaction.Memo,
            CategoryId = transaction.CategoryId,
            TransferAccountId = transaction.TransferAccountId,
            Source = transaction.Source,
            IsApproved = transaction.IsApproved,
            Tags = tags?.ToArray() ?? [],
            Splits = transaction.Splits
                .Select(s => new SnapshotSplit(s.CategoryId, s.Amount, s.Memo, s.TransferAccountId))
                .ToArray(),
        };
    }

    /// <summary>The normalized form of <see cref="EffectivePayee"/> (<see cref="PayeeNormalizer"/>).</summary>
    public string NormalizedPayee() => PayeeNormalizer.Normalize(EffectivePayee);

    /// <inheritdoc />
    public bool Equals(TransactionSnapshot? other) =>
        other is not null
        && Id == other.Id
        && AccountId == other.AccountId
        && Date == other.Date
        && Amount == other.Amount
        && string.Equals(Currency, other.Currency, StringComparison.Ordinal)
        && string.Equals(PayeeRaw, other.PayeeRaw, StringComparison.Ordinal)
        && string.Equals(Payee, other.Payee, StringComparison.Ordinal)
        && PayeeId == other.PayeeId
        && string.Equals(Memo, other.Memo, StringComparison.Ordinal)
        && CategoryId == other.CategoryId
        && TransferAccountId == other.TransferAccountId
        && Source == other.Source
        && IsApproved == other.IsApproved
        && IsFlagged == other.IsFlagged
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal)
        && Splits.SequenceEqual(other.Splits);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, AccountId, Date, Amount, Payee, CategoryId, Memo);
}

/// <summary>One split line of a <see cref="TransactionSnapshot"/>.</summary>
/// <param name="CategoryId">Category of the split.</param>
/// <param name="Amount">Signed minor units.</param>
/// <param name="Memo">Split memo.</param>
/// <param name="TransferAccountId">Transfer target of the split.</param>
public sealed record SnapshotSplit(Guid? CategoryId, long Amount, string? Memo = null, Guid? TransferAccountId = null);
