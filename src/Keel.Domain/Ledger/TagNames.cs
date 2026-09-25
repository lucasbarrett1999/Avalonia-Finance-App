namespace Keel.Domain.Ledger;

/// <summary>
/// Tag name handling (F-TXN-8): tags are free-form, compared case-insensitively, and stored with the
/// spelling first used. <see cref="Flagged"/> is reserved: rules store their "flag" action as this tag.
/// </summary>
public static class TagNames
{
    /// <summary>Maximum stored length of a tag name (the column is 100 characters).</summary>
    public const int MaxLength = 100;

    /// <summary>The reserved tag behind the rules' "flag" action (ADR 0026).</summary>
    public const string Flagged = "Flagged";

    /// <summary>Trims, collapses whitespace, drops a leading '#', and cuts to <see cref="MaxLength"/>; empty for null.</summary>
    public static string Clean(string? name)
    {
        var cleaned = PayeeNames.Clean(name?.Trim().TrimStart('#'));
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd() : cleaned;
    }

    /// <summary>Whether two tag names are the same tag (case-insensitive after cleaning).</summary>
    public static bool Same(string? a, string? b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is the reserved <see cref="Flagged"/> tag.</summary>
    public static bool IsReserved(string? name) => Same(name, Flagged);

    /// <summary>Cleans a list of names, drops empty ones and case-insensitive duplicates, keeps the first spelling and the order.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in names)
        {
            var clean = Clean(name);
            if (clean.Length > 0 && seen.Add(clean))
            {
                result.Add(clean);
            }
        }

        return result;
    }
}
