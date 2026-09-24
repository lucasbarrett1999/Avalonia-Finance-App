using System.Text;

namespace Keel.Domain.Ledger;

/// <summary>
/// Payee name handling for manual entry: display clean-up and the normalized key that makes
/// <see cref="Entities.Payee.NormalizedName"/> unique. (Import-time normalization that strips
/// card-processor noise is a separate, stronger step owned by the import pipeline, PRD 6.5.)
/// </summary>
public static class PayeeNames
{
    /// <summary>Maximum stored length of a payee name.</summary>
    public const int MaxLength = 200;

    /// <summary>Trims and collapses internal whitespace; returns an empty string for null.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var c in name.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        var cleaned = builder.ToString();
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd() : cleaned;
    }

    /// <summary>The unique matching key: cleaned and upper-cased (invariant).</summary>
    public static string Normalize(string? name) => Clean(name).ToUpperInvariant();
}
