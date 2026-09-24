namespace Keel.Domain.Import;

/// <summary>
/// The fields of an incoming transaction that deduplication (PRD 6.5) looks at. File parsers,
/// bank sync and manual entry all produce records with this shape.
/// </summary>
public interface IImportRecord
{
    /// <summary>Ledger date.</summary>
    DateOnly Date { get; }

    /// <summary>Signed amount in minor units; outflows negative.</summary>
    long Amount { get; }

    /// <summary>Payee exactly as the source spelled it.</summary>
    string PayeeRaw { get; }

    /// <summary>Provider or file transaction id (Plaid <c>transaction_id</c>, OFX <c>FITID</c>, SimpleFIN <c>id</c>).</summary>
    string? ProviderTransactionId { get; }

    /// <summary>For a posted row: the provider id of the pending row it replaces (Plaid <c>pending_transaction_id</c>).</summary>
    string? PendingTransactionId { get; }
}
