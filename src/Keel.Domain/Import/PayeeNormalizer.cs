using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Keel.Domain.Import;

/// <summary>
/// Turns a raw bank descriptor into the normalized payee used for deduplication fingerprints,
/// fuzzy matching and (later) payee rules and the learner (PRD 6.5, F-TXN-1 step 1).
/// </summary>
/// <remarks>
/// Steps, in order:
/// <list type="number">
/// <item>ASCII-fold (strip diacritics, map ß/Æ/Ø/Œ/Ł and typographic punctuation) and upper-case.</item>
/// <item>Apply merchant rewrites from <see cref="PayeeNoiseTable.Rewrites"/> (<c>AMZN MKTP</c> → <c>AMAZON</c>).</item>
/// <item>Remove processor prefixes such as <c>SQ *</c>, <c>TST*</c>, <c>PAYPAL *</c>.</item>
/// <item>Remove reference numbers, masked card numbers, short dates and web decoration.</item>
/// <item>Split into words; drop noise phrases and words, digit-only words of length ≥ 4, and
/// reference-like words (at least 5 digits, mostly digits).</item>
/// <item>Collapse whitespace.</item>
/// </list>
/// If nothing is left (a descriptor that is all noise), the folded, upper-case, whitespace-collapsed
/// input is returned instead, so distinct descriptors never collapse to an empty payee.
/// </remarks>
public static partial class PayeeNormalizer
{
    /// <summary>
    /// Version of the normalization rules. Stored fingerprints were computed with some version;
    /// bump this when <see cref="PayeeNoiseTable"/> or this algorithm changes.
    /// </summary>
    public const int Version = 1;

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly (Regex Pattern, string Replacement)[] Rewrites =
        PayeeNoiseTable.Rewrites.Select(r => (new Regex(r.Pattern, Options), r.Replacement)).ToArray();

    private static readonly Regex ProcessorPrefix = new(
        "^(?:(?:" + string.Join('|', PayeeNoiseTable.ProcessorPrefixes.Select(Regex.Escape)) + @")\s*\*\s*)+",
        Options);

    private static readonly Regex[] Removals =
        PayeeNoiseTable.RemovalPatterns.Select(p => new Regex(p, Options)).ToArray();

    private static readonly HashSet<string> NoiseTokens = new(PayeeNoiseTable.NoiseTokens, StringComparer.Ordinal);

    private static readonly string[][] NoisePhrases = PayeeNoiseTable.NoisePhrases
        .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .OrderByDescending(p => p.Length)
        .ToArray();

    /// <summary>Normalizes a raw payee descriptor (see the type remarks). Null yields an empty string.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var folded = FoldToAsciiUpper(raw);
        var text = folded;
        foreach (var (pattern, replacement) in Rewrites)
        {
            text = pattern.Replace(text, replacement);
        }

        text = ProcessorPrefix.Replace(text.TrimStart(), string.Empty);
        foreach (var removal in Removals)
        {
            text = removal.Replace(text, " ");
        }

        var tokens = SplitTokens(text);
        var kept = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var phraseLength = MatchPhrase(tokens, i);
            if (phraseLength > 0)
            {
                i += phraseLength - 1;
                continue;
            }

            if (!IsNoise(tokens[i]))
            {
                kept.Add(tokens[i]);
            }
        }

        return kept.Count > 0 ? string.Join(' ', kept) : CollapseWhitespace(folded);
    }

    /// <summary>
    /// ASCII-folds and upper-cases: diacritics removed (<c>é</c> → <c>E</c>), ligatures and
    /// special letters expanded (<c>ß</c> → <c>SS</c>), typographic quotes and dashes mapped to
    /// ASCII, any other non-ASCII character replaced by a space.
    /// </summary>
    public static string FoldToAsciiUpper(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (c < 128)
            {
                sb.Append(char.ToUpperInvariant(c));
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(c switch
            {
                'ß' => "SS",
                'æ' or 'Æ' => "AE",
                'œ' or 'Œ' => "OE",
                'ø' or 'Ø' => "O",
                'ł' or 'Ł' => "L",
                'đ' or 'Đ' => "D",
                'þ' or 'Þ' => "TH",
                'ð' or 'Ð' => "D",
                'ı' => "I",
                '‘' or '’' or '‚' or '′' => "'",
                '“' or '”' or '„' or '″' => "\"",
                '‐' or '‑' or '‒' or '–' or '—' or '−' => "-",
                '…' => "...",
                _ => " ",
            });
        }

        return sb.ToString();
    }

    private static List<string> SplitTokens(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\'' or '.':
                    break; // JOE'S → JOES, U.S. → US
                case (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '&' or '-' or '#':
                    sb.Append(c);
                    break;
                default:
                    sb.Append(' '); // '*', '/', ',', parentheses, quotes and all other punctuation
                    break;
            }
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('-'))
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static int MatchPhrase(List<string> tokens, int start)
    {
        foreach (var phrase in NoisePhrases)
        {
            if (start + phrase.Length > tokens.Count)
            {
                continue;
            }

            var match = true;
            for (var j = 0; j < phrase.Length && match; j++)
            {
                match = string.Equals(tokens[start + j], phrase[j], StringComparison.Ordinal);
            }

            if (match)
            {
                return phrase.Length;
            }
        }

        return 0;
    }

    private static bool IsNoise(string token)
    {
        if (NoiseTokens.Contains(token) || token.All(c => c is '#' or '-' or '&'))
        {
            return true;
        }

        var digits = token.Count(char.IsAsciiDigit);
        if (digits == token.Length && digits >= 4)
        {
            return true; // digit-only reference numbers (PRD 6.5)
        }

        // Reference-like codes such as S466005123456789 or 12345-U.
        return digits >= 5 && digits * 10 >= token.Length * 6;
    }

    private static string CollapseWhitespace(string text) =>
        WhitespaceRun().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
