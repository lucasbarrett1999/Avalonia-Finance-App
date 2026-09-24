namespace Keel.Application.Import;

/// <summary>
/// Per-account import memory (F-TXN-2): the last CSV column mapping with the header it was made
/// for, and the folder the last file came from. Stored in the budget file's <c>Setting</c> table
/// (ADR 0051); not part of the ledger, so not audited and not undone.
/// </summary>
public interface IImportSettingsStore
{
    /// <summary>The remembered CSV mapping of an account, or null.</summary>
    Task<RememberedCsvMapping?> GetCsvMappingAsync(Guid accountId, CancellationToken ct);

    /// <summary>Remembers the CSV mapping used for an account.</summary>
    Task SaveCsvMappingAsync(Guid accountId, RememberedCsvMapping mapping, CancellationToken ct);

    /// <summary>The folder of the last file imported into an account, or null.</summary>
    Task<string?> GetLastFolderAsync(Guid accountId, CancellationToken ct);

    /// <summary>Remembers the folder of the last file imported into an account.</summary>
    Task SaveLastFolderAsync(Guid accountId, string folder, CancellationToken ct);
}

/// <summary>A CSV mapping remembered for an account, with the header row it was made for.</summary>
/// <param name="Mapping">The mapping.</param>
/// <param name="Headers">Header names (or <c>Column 1</c>, ... for headerless files) of the file it was made for.</param>
public sealed record RememberedCsvMapping(CsvColumnMapping Mapping, IReadOnlyList<string> Headers)
{
    /// <summary>
    /// Whether the mapping fits a file with <paramref name="headers"/>: same number of columns and
    /// the same names (ignoring case and surrounding spaces). A different layout gets detection instead.
    /// </summary>
    public bool Fits(IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return headers.Count == Headers.Count
            && headers.Zip(Headers).All(p => string.Equals(p.First.Trim(), p.Second.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
