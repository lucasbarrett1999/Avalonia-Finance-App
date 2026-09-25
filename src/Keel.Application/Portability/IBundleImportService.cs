namespace Keel.Application.Portability;

/// <summary>
/// Lossless re-import of a JSON bundle (F-REP-6) into a new budget file: every user-owned table with its ids,
/// written in one database transaction through one prepared command per table that also records audit rows
/// (ADR 0052 style), after foreign keys and the split-sum invariant are validated. Never into a file that
/// already holds data (ADR 0098).
/// </summary>
public interface IBundleImportService
{
    /// <summary>Reads and checks a bundle's header without reading its rows.</summary>
    /// <exception cref="BundleException">Not a Keel bundle, or made by a newer Keel.</exception>
    Task<BundleInfo> ReadInfoAsync(string bundlePath, CancellationToken ct);

    /// <summary>
    /// Creates <paramref name="targetPath"/> (or uses it when it is an empty budget file), imports the bundle into it
    /// and restores the bundle's attachment files next to it. The target is not opened in any session; the caller
    /// opens it afterwards. On failure nothing is committed and a file created by this call is deleted.
    /// </summary>
    /// <exception cref="BundleException">The bundle is invalid, newer, or the target already holds data.</exception>
    Task<BundleImportResult> ImportIntoNewFileAsync(string bundlePath, string targetPath, CancellationToken ct);
}

/// <summary>What a bundle import wrote.</summary>
/// <param name="Path">The budget file.</param>
/// <param name="Rows">Rows written per table (by EF entity name).</param>
/// <param name="AttachmentFiles">Attachment files restored.</param>
public sealed record BundleImportResult(string Path, IReadOnlyDictionary<string, long> Rows, int AttachmentFiles)
{
    /// <summary>Rows of <paramref name="entity"/> (0 when absent).</summary>
    public long Count(string entity) => Rows.TryGetValue(entity, out var n) ? n : 0;
}

/// <summary>Why a bundle cannot be imported; the UI maps <see cref="Code"/> to a message.</summary>
public sealed class BundleException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BundleException(BundleError code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Creates the exception.</summary>
    public BundleException()
        : this(BundleError.NotABundle, "The file is not a Keel export bundle.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public BundleException(string message)
        : this(BundleError.NotABundle, message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public BundleException(string message, Exception innerException)
        : this(BundleError.NotABundle, message, innerException)
    {
    }

    /// <summary>What is wrong.</summary>
    public BundleError Code { get; }
}

/// <summary>Kinds of bundle problems.</summary>
public enum BundleError
{
    /// <summary>The file is not a Keel export bundle (or is damaged).</summary>
    NotABundle,

    /// <summary>The bundle was written by a newer Keel (newer format version, tables or columns this build does not know).</summary>
    TooNew,

    /// <summary>The bundle's rows break the budget file's rules (a missing referenced row, splits that do not add up).</summary>
    Invalid,

    /// <summary>The target budget file already holds data.</summary>
    TargetNotEmpty,
}
