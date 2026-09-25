namespace Keel.Desktop.Services;

/// <summary>
/// Fuzzy matching for the command palette: the query's characters must appear in order (case-insensitive);
/// matches at word starts, consecutive runs and a prefix score higher.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Score of <paramref name="text"/> for <paramref name="query"/>; null when it does not match. An empty query matches everything with 0.</summary>
    public static int? Score(string query, string text)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);
        var q = query.Trim();
        if (q.Length == 0)
        {
            return 0;
        }

        var score = 0;
        var t = 0;
        var previousMatch = -2;
        foreach (var raw in q)
        {
            if (char.IsWhiteSpace(raw))
            {
                continue;
            }

            var c = char.ToLowerInvariant(raw);
            var found = -1;
            for (; t < text.Length; t++)
            {
                if (char.ToLowerInvariant(text[t]) == c)
                {
                    found = t;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            score += 1;
            if (found == 0)
            {
                score += 8;
            }
            else if (!char.IsLetterOrDigit(text[found - 1]))
            {
                score += 6;
            }

            if (found == previousMatch + 1)
            {
                score += 4;
            }

            previousMatch = found;
            t = found + 1;
        }

        // Prefer shorter texts among equal matches.
        return (score * 100) - Math.Min(text.Length, 99);
    }

    /// <summary>Filters and orders <paramref name="items"/> by score (stable for ties).</summary>
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string? query, Func<T, string> text)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(query))
        {
            return items.ToList();
        }

        return items
            .Select((item, index) => (Item: item, Index: index, Score: Score(query, text(item))))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToList();
    }
}
