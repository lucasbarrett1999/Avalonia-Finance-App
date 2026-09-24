using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

/// <summary>A minimal <see cref="IImportRecord"/> for tests.</summary>
public sealed record ImportRow(
    DateOnly Date,
    long Amount,
    string PayeeRaw,
    string? ProviderTransactionId = null,
    string? PendingTransactionId = null) : IImportRecord;
