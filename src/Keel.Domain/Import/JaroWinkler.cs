namespace Keel.Domain.Import;

/// <summary>
/// Jaro-Winkler string similarity (Winkler 1990), used for fuzzy payee matching in PRD 6.5 step 4.
/// Returns 1 for identical strings and 0 for strings with nothing in common.
/// </summary>
/// <remarks>
/// Standard parameters: match window <c>max(|a|, |b|) / 2 - 1</c>, prefix scale 0.1 over at most
/// four leading characters, and the prefix bonus applied only when the Jaro similarity exceeds
/// 0.7 (Winkler's boost threshold). Comparison is ordinal; normalize inputs first.
/// </remarks>
public static class JaroWinkler
{
    private const double PrefixScale = 0.1;
    private const int MaxPrefix = 4;
    private const double BoostThreshold = 0.7;

    /// <summary>Jaro-Winkler similarity in [0, 1].</summary>
    public static double Similarity(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var jaro = Jaro(a, b);
        if (jaro <= BoostThreshold)
        {
            return jaro;
        }

        var prefix = 0;
        var limit = Math.Min(MaxPrefix, Math.Min(a.Length, b.Length));
        while (prefix < limit && a[prefix] == b[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * PrefixScale * (1 - jaro));
    }

    /// <summary>Plain Jaro similarity in [0, 1].</summary>
    public static double Jaro(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        var window = Math.Max(0, (Math.Max(a.Length, b.Length) / 2) - 1);
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(b.Length - 1, i + window);
            for (var j = from; j <= to; j++)
            {
                if (!bMatched[j] && a[i] == b[j])
                {
                    aMatched[i] = true;
                    bMatched[j] = true;
                    matches++;
                    break;
                }
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        // Count half-transpositions: matched characters that appear in a different order.
        var outOfOrder = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i])
            {
                continue;
            }

            while (!bMatched[k])
            {
                k++;
            }

            if (a[i] != b[k])
            {
                outOfOrder++;
            }

            k++;
        }

        double m = matches;
        var transpositions = outOfOrder / 2.0;
        return ((m / a.Length) + (m / b.Length) + ((m - transpositions) / m)) / 3.0;
    }
}
