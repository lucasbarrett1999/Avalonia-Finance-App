namespace Keel.Domain;

/// <summary>
/// ISO 4217 helpers: code validation and the number of minor-unit digits per currency.
/// </summary>
public static class Currency
{
    /// <summary>The default base currency for a new budget file.</summary>
    public const string Default = "USD";

    // ISO 4217 currencies whose minor unit is not 2 digits. Everything else uses 2.
    private static readonly Dictionary<string, int> NonStandardMinorDigits = new(StringComparer.Ordinal)
    {
        ["BIF"] = 0,
        ["CLP"] = 0,
        ["DJF"] = 0,
        ["GNF"] = 0,
        ["ISK"] = 0,
        ["JPY"] = 0,
        ["KMF"] = 0,
        ["KRW"] = 0,
        ["PYG"] = 0,
        ["RWF"] = 0,
        ["UGX"] = 0,
        ["UYI"] = 0,
        ["VND"] = 0,
        ["VUV"] = 0,
        ["XAF"] = 0,
        ["XOF"] = 0,
        ["XPF"] = 0,
        ["BHD"] = 3,
        ["IQD"] = 3,
        ["JOD"] = 3,
        ["KWD"] = 3,
        ["LYD"] = 3,
        ["OMR"] = 3,
        ["TND"] = 3,
        ["CLF"] = 4,
        ["UYW"] = 4,
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["JPY"] = "¥",
        ["INR"] = "₹",
        ["KRW"] = "₩",
        ["CAD"] = "CA$",
        ["AUD"] = "A$",
        ["NZD"] = "NZ$",
        ["CHF"] = "CHF",
        ["MXN"] = "MX$",
    };

    /// <summary>Returns true when <paramref name="code"/> is three upper-case ASCII letters.</summary>
    public static bool IsValidCode(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');

    /// <summary>Normalizes a currency code to upper case and validates its shape.</summary>
    /// <exception cref="ArgumentException">The code is not a three-letter ISO 4217 code.</exception>
    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var upper = code.Trim().ToUpperInvariant();
        if (!IsValidCode(upper))
        {
            throw new ArgumentException($"'{code}' is not a three-letter ISO 4217 currency code.", nameof(code));
        }

        return upper;
    }

    /// <summary>Number of digits after the decimal separator for the currency's minor unit.</summary>
    public static int MinorUnitDigits(string code) =>
        NonStandardMinorDigits.TryGetValue(code, out var digits) ? digits : 2;

    /// <summary>10^digits: how many minor units make one major unit.</summary>
    public static long MinorUnitsPerMajor(string code) => MinorUnitDigits(code) switch
    {
        0 => 1,
        1 => 10,
        2 => 100,
        3 => 1_000,
        4 => 10_000,
        var d => (long)Math.Pow(10, d),
    };

    /// <summary>A display symbol for the currency; falls back to the ISO code.</summary>
    public static string Symbol(string code) => Symbols.TryGetValue(code, out var symbol) ? symbol : code;
}
