using System.Globalization;
using System.Text;

namespace Keel.Domain.Ledger;

/// <summary>
/// A parsed register search (F-TXN-7), e.g.
/// <c>amount:&gt;100 category:groceries date:2026-08 payee:"trader joe"</c>.
/// Free words match payee, memo, category and account names. Keys: <c>payee</c>, <c>memo</c>,
/// <c>category</c>, <c>account</c>, <c>tag</c> (contains, case-insensitive); <c>amount</c>
/// (magnitude: <c>100</c>, <c>&gt;100</c>, <c>&gt;=100</c>, <c>&lt;100</c>, <c>&lt;=100</c>,
/// <c>10..20</c>); <c>date</c> (<c>2026</c>, <c>2026-08</c>, <c>2026-08-15</c>, ranges with
/// <c>..</c>, and <c>&gt;</c>/<c>&lt;</c> bounds). Amounts use the invariant decimal point.
/// </summary>
public sealed record SearchQuery
{
    /// <summary>Free-text terms.</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    /// <summary>Payee contains any of these.</summary>
    public IReadOnlyList<string> Payees { get; init; } = [];

    /// <summary>Memo contains any of these.</summary>
    public IReadOnlyList<string> Memos { get; init; } = [];

    /// <summary>Category (or a split's category) contains any of these.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Account name contains any of these.</summary>
    public IReadOnlyList<string> Accounts { get; init; } = [];

    /// <summary>Tag name contains any of these.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Lower bound of the amount magnitude in major units.</summary>
    public decimal? AmountMin { get; init; }

    /// <summary>Whether <see cref="AmountMin"/> is inclusive.</summary>
    public bool AmountMinInclusive { get; init; } = true;

    /// <summary>Upper bound of the amount magnitude in major units.</summary>
    public decimal? AmountMax { get; init; }

    /// <summary>Whether <see cref="AmountMax"/> is inclusive.</summary>
    public bool AmountMaxInclusive { get; init; } = true;

    /// <summary>First date (inclusive).</summary>
    public DateOnly? DateFrom { get; init; }

    /// <summary>Last date (inclusive).</summary>
    public DateOnly? DateTo { get; init; }

    /// <summary>Tokens that could not be parsed (shown to the user; otherwise ignored).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Whether the query has no criteria.</summary>
    public bool IsEmpty => Terms.Count == 0 && Payees.Count == 0 && Memos.Count == 0 && Categories.Count == 0
        && Accounts.Count == 0 && Tags.Count == 0 && AmountMin is null && AmountMax is null && DateFrom is null && DateTo is null;

    /// <summary>An empty query.</summary>
    public static SearchQuery Empty { get; } = new();

    /// <summary>Parses the query text; never throws.</summary>
    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var terms = new List<string>();
        var payees = new List<string>();
        var memos = new List<string>();
        var categories = new List<string>();
        var accounts = new List<string>();
        var tags = new List<string>();
        var errors = new List<string>();
        var query = new SearchQuery();

        foreach (var token in Tokenize(text))
        {
            var colon = token.Key;
            if (colon is null)
            {
                terms.Add(token.Value);
                continue;
            }

            var value = token.Value;
            switch (colon)
            {
                case "payee":
                    payees.Add(value);
                    break;
                case "memo":
                    memos.Add(value);
                    break;
                case "category":
                case "cat":
                    categories.Add(value);
                    break;
                case "account":
                case "acct":
                    accounts.Add(value);
                    break;
                case "tag":
                    tags.Add(value);
                    break;
                case "amount":
                case "amt":
                    if (!TryApplyAmount(value, ref query))
                    {
                        errors.Add(token.Raw);
                    }

                    break;
                case "date":
                    if (!TryApplyDate(value, ref query))
                    {
                        errors.Add(token.Raw);
                    }

                    break;
                default:
                    terms.Add(token.Raw);
                    break;
            }
        }

        return query with
        {
            Terms = terms,
            Payees = payees,
            Memos = memos,
            Categories = categories,
            Accounts = accounts,
            Tags = tags,
            Errors = errors,
        };
    }

    private static bool TryApplyAmount(string value, ref SearchQuery query)
    {
        if (value.Contains("..", StringComparison.Ordinal))
        {
            var parts = value.Split("..", 2);
            if (!TryAmount(parts[0], out var min) || !TryAmount(parts[1], out var max))
            {
                return false;
            }

            query = query with { AmountMin = Math.Min(min, max), AmountMax = Math.Max(min, max) };
            return true;
        }

        var (op, rest) = SplitOperator(value);
        if (!TryAmount(rest, out var amount))
        {
            return false;
        }

        query = op switch
        {
            ">" => query with { AmountMin = amount, AmountMinInclusive = false },
            ">=" => query with { AmountMin = amount, AmountMinInclusive = true },
            "<" => query with { AmountMax = amount, AmountMaxInclusive = false },
            "<=" => query with { AmountMax = amount, AmountMaxInclusive = true },
            _ => query with { AmountMin = amount, AmountMax = amount, AmountMinInclusive = true, AmountMaxInclusive = true },
        };
        return true;
    }

    private static bool TryApplyDate(string value, ref SearchQuery query)
    {
        if (value.Contains("..", StringComparison.Ordinal))
        {
            var parts = value.Split("..", 2);
            if (!TryDateRange(parts[0], out var first, out _) || !TryDateRange(parts[1], out _, out var last))
            {
                return false;
            }

            query = query with { DateFrom = first, DateTo = last };
            return true;
        }

        var (op, rest) = SplitOperator(value);
        if (!TryDateRange(rest, out var from, out var to))
        {
            return false;
        }

        query = op switch
        {
            ">" => query with { DateFrom = to.AddDays(1) },
            ">=" => query with { DateFrom = from },
            "<" => query with { DateTo = from.AddDays(-1) },
            "<=" => query with { DateTo = to },
            _ => query with { DateFrom = from, DateTo = to },
        };
        return true;
    }

    private static (string Op, string Remainder) SplitOperator(string value)
    {
        foreach (var op in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (value.StartsWith(op, StringComparison.Ordinal))
            {
                return (op, value[op.Length..]);
            }
        }

        return (string.Empty, value);
    }

    private static bool TryAmount(string text, out decimal amount)
    {
        var cleaned = text.Trim().TrimStart('$', '€', '£').Replace(",", string.Empty, StringComparison.Ordinal);
        var ok = decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
        amount = Math.Abs(amount);
        return ok;
    }

    /// <summary>Parses "2026", "2026-08" or "2026-08-15" into the first and last day it covers.</summary>
    private static bool TryDateRange(string text, out DateOnly first, out DateOnly last)
    {
        first = last = default;
        var parts = text.Trim().Split('-');
        var ok = parts.Length switch
        {
            1 => int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1 and <= 9999
                && Set(new DateOnly(y, 1, 1), new DateOnly(y, 12, 31), out first, out last),
            2 => int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y2)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m2)
                && y2 is >= 1 and <= 9999 && m2 is >= 1 and <= 12
                && Set(new DateOnly(y2, m2, 1), new DateOnly(y2, m2, DateTime.DaysInMonth(y2, m2)), out first, out last),
            3 => DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && Set(d, d, out first, out last),
            _ => false,
        };
        return ok;
    }

    private static bool Set(DateOnly a, DateOnly b, out DateOnly first, out DateOnly last)
    {
        first = a;
        last = b;
        return true;
    }

    private static IEnumerable<Token> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                yield break;
            }

            var raw = new StringBuilder();
            var value = new StringBuilder();
            string? key = null;
            var inQuotes = false;
            while (i < text.Length && (inQuotes || !char.IsWhiteSpace(text[i])))
            {
                var c = text[i++];
                raw.Append(c);
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == ':' && key is null && !inQuotes && value.Length > 0 && IsKey(value.ToString()))
                {
                    key = value.ToString().ToLowerInvariant();
                    value.Clear();
                    continue;
                }

                value.Append(c);
            }

            if (value.Length > 0 || key is not null)
            {
                yield return new Token(key, value.ToString().Trim(), raw.ToString());
            }
        }
    }

    private static bool IsKey(string candidate) => candidate.All(char.IsAsciiLetter);

    private readonly record struct Token(string? Key, string Value, string Raw);
}
