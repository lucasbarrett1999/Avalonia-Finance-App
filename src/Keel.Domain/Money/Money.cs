using System.Globalization;

namespace Keel.Domain;

/// <summary>
/// An amount of money in integer minor units (cents for USD) of one ISO 4217 currency.
/// All arithmetic is integer arithmetic; <see cref="decimal"/> appears only in the parsing
/// and formatting helpers used at UI boundaries (PRD 6.1).
/// Sign convention: outflows are negative, inflows are positive.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Creates money from minor units.</summary>
    /// <param name="amount">Amount in minor units, e.g. 1234 for $12.34.</param>
    /// <param name="currency">ISO 4217 code; normalized to upper case.</param>
    public Money(long amount, string currency)
    {
        Amount = amount;
        Currency = Keel.Domain.Currency.Normalize(currency);
    }

    /// <summary>Amount in minor units.</summary>
    public long Amount { get; }

    /// <summary>ISO 4217 currency code, upper case.</summary>
    public string Currency { get; }

    /// <summary>True when the amount is zero.</summary>
    public bool IsZero => Amount == 0;

    /// <summary>True when the amount is negative (an outflow).</summary>
    public bool IsNegative => Amount < 0;

    /// <summary>True when the amount is positive (an inflow).</summary>
    public bool IsPositive => Amount > 0;

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(string currency) => new(0, currency);

    /// <summary>Creates money from minor units.</summary>
    public static Money FromMinorUnits(long amount, string currency) => new(amount, currency);

    /// <summary>
    /// Converts a decimal major-unit value (e.g. 12.345 dollars) to minor units using banker's
    /// rounding (midpoint to even). Only for UI boundaries and file import.
    /// </summary>
    /// <exception cref="OverflowException">The value does not fit in a 64-bit minor-unit amount.</exception>
    public static Money FromDecimal(decimal value, string currency)
    {
        var code = Keel.Domain.Currency.Normalize(currency);
        var scaled = decimal.Round(value * Keel.Domain.Currency.MinorUnitsPerMajor(code), 0, MidpointRounding.ToEven);
        return new Money(decimal.ToInt64(scaled), code);
    }

    /// <summary>The amount in major units as a decimal (for display and export only).</summary>
    public decimal ToDecimal() => (decimal)Amount / Keel.Domain.Currency.MinorUnitsPerMajor(Currency);

    /// <summary>Absolute value.</summary>
    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>Negation (inflow becomes outflow and vice versa).</summary>
    public Money Negate() => new(checked(-Amount), Currency);

    /// <summary>Multiplies by an integer factor with overflow checking.</summary>
    public Money Multiply(long factor) => new(checked(Amount * factor), Currency);

    /// <summary>
    /// Splits this amount by percentages. Each part is rounded with banker's rounding and the
    /// rounding remainder goes to the last part, so the parts always sum exactly to this amount
    /// (PRD 6.1).
    /// </summary>
    /// <param name="percentages">Percentages, e.g. 50, 30, 20. They need not sum to 100; the last
    /// part absorbs whatever is left.</param>
    public IReadOnlyList<Money> SplitByPercentages(IReadOnlyList<decimal> percentages)
    {
        ArgumentNullException.ThrowIfNull(percentages);
        if (percentages.Count == 0)
        {
            throw new ArgumentException("At least one percentage is required.", nameof(percentages));
        }

        var parts = new Money[percentages.Count];
        long allocated = 0;
        for (var i = 0; i < percentages.Count - 1; i++)
        {
            var part = decimal.ToInt64(decimal.Round(Amount * percentages[i] / 100m, 0, MidpointRounding.ToEven));
            parts[i] = new Money(part, Currency);
            allocated = checked(allocated + part);
        }

        parts[^1] = new Money(checked(Amount - allocated), Currency);
        return parts;
    }

    /// <summary>
    /// Splits this amount proportionally to integer weights. Each part is rounded down toward
    /// zero and the remainder goes to the last part, so the parts sum exactly to this amount.
    /// </summary>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<long> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        if (weights.Any(w => w < 0))
        {
            throw new ArgumentException("Weights must not be negative.", nameof(weights));
        }

        var total = weights.Sum(w => (decimal)w);
        if (total == 0)
        {
            throw new ArgumentException("At least one weight must be positive.", nameof(weights));
        }

        var parts = new Money[weights.Count];
        long allocated = 0;
        for (var i = 0; i < weights.Count - 1; i++)
        {
            var part = decimal.ToInt64(decimal.Truncate(Amount * (decimal)weights[i] / total));
            parts[i] = new Money(part, Currency);
            allocated = checked(allocated + part);
        }

        parts[^1] = new Money(checked(Amount - allocated), Currency);
        return parts;
    }

    /// <summary>
    /// Formats as a currency string in the given culture (default: current culture), with the
    /// currency's minor-unit digits, e.g. "$1,234.56" or "-$12.00".
    /// </summary>
    public string Format(IFormatProvider? provider = null)
    {
        var culture = provider as CultureInfo ?? CultureInfo.CurrentCulture;
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencyDecimalDigits = Keel.Domain.Currency.MinorUnitDigits(Currency);
        format.CurrencySymbol = ResolveSymbol(culture, Currency);
        return ToDecimal().ToString("C", format);
    }

    /// <summary>Formats as a plain number with the currency's minor-unit digits, e.g. "1,234.56".</summary>
    public string FormatNumber(IFormatProvider? provider = null)
    {
        var digits = Keel.Domain.Currency.MinorUnitDigits(Currency);
        return ToDecimal().ToString("N" + digits.ToString(CultureInfo.InvariantCulture), provider ?? CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Parses user or file input such as "12.50", "-1,234.5", "$12", "(12.00)" into money.
    /// Excess fraction digits are rounded with banker's rounding.
    /// </summary>
    public static bool TryParse(string? text, string currency, IFormatProvider? provider, out Money result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text) || !Keel.Domain.Currency.IsValidCode(currency?.Trim().ToUpperInvariant()))
        {
            return false;
        }

        var culture = provider as CultureInfo ?? CultureInfo.CurrentCulture;
        var code = Keel.Domain.Currency.Normalize(currency!);
        var cleaned = text.Trim();
        var negative = false;

        if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
        {
            negative = true;
            cleaned = cleaned[1..^1].Trim();
        }

        cleaned = cleaned
            .Replace(ResolveSymbol(culture, code), string.Empty, StringComparison.Ordinal)
            .Replace(culture.NumberFormat.CurrencySymbol, string.Empty, StringComparison.Ordinal)
            .Replace(code, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign
            | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowLeadingWhite
            | NumberStyles.AllowTrailingWhite;

        if (!decimal.TryParse(cleaned, styles, culture, out var value))
        {
            return false;
        }

        try
        {
            result = FromDecimal(negative ? -value : value, code);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Parses input; throws <see cref="FormatException"/> when it is not a valid amount.</summary>
    public static Money Parse(string text, string currency, IFormatProvider? provider = null) =>
        TryParse(text, currency, provider, out var result)
            ? result
            : throw new FormatException($"'{text}' is not a valid amount.");

    /// <inheritdoc />
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Invariant, unambiguous representation for logs and tests, e.g. "12.34 USD".</summary>
    public override string ToString() =>
        ToDecimal().ToString("F" + Keel.Domain.Currency.MinorUnitDigits(Currency).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        + " " + Currency;

    /// <summary>Adds two amounts of the same currency.</summary>
    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Amount + right.Amount), left.Currency);
    }

    /// <summary>Subtracts two amounts of the same currency.</summary>
    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.Amount - right.Amount), left.Currency);
    }

    /// <summary>Negation.</summary>
    public static Money operator -(Money value) => value.Negate();

    /// <summary>Multiplication by an integer factor.</summary>
    public static Money operator *(Money value, long factor) => value.Multiply(factor);

    /// <summary>Less than (same currency only).</summary>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Greater than (same currency only).</summary>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Less than or equal (same currency only).</summary>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Greater than or equal (same currency only).</summary>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>Sums a sequence of amounts that share one currency.</summary>
    public static Money Sum(IEnumerable<Money> values, string currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        var total = Zero(currency);
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }

    private static string ResolveSymbol(CultureInfo culture, string code)
    {
        if (!culture.IsNeutralCulture && !Equals(culture, CultureInfo.InvariantCulture))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (string.Equals(region.ISOCurrencySymbol, code, StringComparison.Ordinal))
                {
                    return region.CurrencySymbol;
                }
            }
            catch (ArgumentException)
            {
                // Culture has no region; fall through to the static table.
            }
        }

        return Keel.Domain.Currency.Symbol(code);
    }
}

/// <summary>Thrown when arithmetic or comparison mixes two currencies (v1 is single-currency, D3).</summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    /// <summary>Creates the exception for the two currencies involved.</summary>
    public CurrencyMismatchException(string left, string right)
        : base($"Cannot combine amounts in {left} and {right}.")
    {
        Left = left;
        Right = right;
    }

    /// <summary>Creates the exception with a default message.</summary>
    public CurrencyMismatchException()
        : this("?", "?")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CurrencyMismatchException(string message)
        : base(message)
    {
        Left = Right = "?";
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public CurrencyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
        Left = Right = "?";
    }

    /// <summary>Currency of the left operand.</summary>
    public string Left { get; }

    /// <summary>Currency of the right operand.</summary>
    public string Right { get; }
}
