using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Application.Import;

namespace Keel.Infrastructure.Stats;

/// <summary>
/// What the Stats page needs from file imports (PRD 4, "duplicates on re-import"), kept in the file's Setting
/// table under <see cref="Key"/>: a fingerprint of each recently imported file (a SHA-256 of its rows, so no
/// payee or amount is readable) and, for files imported again, how many of their rows were flagged duplicate.
/// </summary>
public sealed record ImportStatsLog
{
    /// <summary>Setting key.</summary>
    public const string Key = "stats.imports";

    /// <summary>Files remembered for re-import detection.</summary>
    public const int MaxBatches = 500;

    /// <summary>Re-imports kept.</summary>
    public const int MaxReimports = 200;

    /// <summary>Fingerprints of imported files, oldest first.</summary>
    public IReadOnlyList<string> Batches { get; init; } = [];

    /// <summary>Re-imports, oldest first.</summary>
    public IReadOnlyList<ReimportSample> Reimports { get; init; } = [];

    /// <summary>Records an import of a batch with <paramref name="fingerprint"/>; a known fingerprint is a re-import.</summary>
    public ImportStatsLog Record(string fingerprint, int rows, int flagged, DateTime at)
    {
        if (Batches.Contains(fingerprint, StringComparer.Ordinal))
        {
            return this with { Reimports = [.. Reimports.TakeLast(MaxReimports - 1), new ReimportSample(at, rows, flagged)] };
        }

        return this with { Batches = [.. Batches.TakeLast(MaxBatches - 1), fingerprint] };
    }

    /// <summary>The fingerprint of an imported batch: its account and every row's date, amount, payee, memo and ids.</summary>
    public static string Fingerprint(ImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? text)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            hash.AppendData("\u001f"u8);
        }

        Add(batch.AccountId.ToString("N"));
        foreach (var row in batch.Transactions)
        {
            Add(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(row.Amount.ToString(CultureInfo.InvariantCulture));
            Add(row.PayeeRaw);
            Add(row.Memo);
            Add(row.ProviderTransactionId);
            Add(row.CheckNumber);
            hash.AppendData("\u001e"u8);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>One re-import of a file imported before.</summary>
/// <param name="At">When (UTC).</param>
/// <param name="Rows">Rows in the file.</param>
/// <param name="Flagged">Rows flagged as duplicates and skipped.</param>
public sealed record ReimportSample(DateTime At, int Rows, int Flagged);
