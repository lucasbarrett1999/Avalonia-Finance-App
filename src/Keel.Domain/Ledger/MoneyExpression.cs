using System.Globalization;
using System.Text;

namespace Keel.Domain.Ledger;

/// <summary>
/// Evaluates what a user types into an amount field (PRD 7.3): a number in the user's locale,
/// optionally with a currency symbol, or simple arithmetic such as <c>12.50+3</c>,
/// <c>3*4.99</c>, <c>100/3</c> or <c>(20+5)*2</c>. Evaluation is exact <see cref="decimal"/>
/// arithmetic; callers round to minor units with <see cref="Money.FromDecimal"/>.
/// </summary>
public static class MoneyExpression
{
    /// <summary>Evaluates <paramref name="text"/>; returns false when it is not a valid expression.</summary>
    public static bool TryEvaluate(string? text, CultureInfo culture, out decimal value)
    {
        ArgumentNullException.ThrowIfNull(culture);
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var format = culture.NumberFormat;
        var normalized = Normalize(text, format);
        if (normalized is null)
        {
            return false;
        }

        var parser = new Parser(normalized);
        try
        {
            if (!parser.TryParseExpression(out value) || !parser.AtEnd)
            {
                value = 0;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is OverflowException or DivideByZeroException)
        {
            value = 0;
            return false;
        }
    }

    /// <summary>Whether the text contains an operator after its first character (i.e. is arithmetic).</summary>
    public static bool IsArithmetic(string? text) =>
        !string.IsNullOrEmpty(text) && text.Trim().Skip(1).Any(c => c is '+' or '-' or '*' or '/' or '×' or '÷');

    // Rewrites the input into an invariant form: digits, '.', operators and parentheses only.
    private static string? Normalize(string text, NumberFormatInfo format)
    {
        var s = text;
        foreach (var symbol in new[] { format.CurrencySymbol, "$", "€", "£", "¥" })
        {
            if (!string.IsNullOrEmpty(symbol))
            {
                s = s.Replace(symbol, string.Empty, StringComparison.Ordinal);
            }
        }

        var decimalSeparator = format.NumberDecimalSeparator;
        var groupSeparator = format.NumberGroupSeparator;
        var builder = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ' ' || c == ' ')
            {
                continue;
            }

            if (Matches(s, i, decimalSeparator))
            {
                builder.Append('.');
                i += decimalSeparator.Length - 1;
                continue;
            }

            if (groupSeparator.Length > 0 && !string.IsNullOrWhiteSpace(groupSeparator) && Matches(s, i, groupSeparator))
            {
                i += groupSeparator.Length - 1;
                continue;
            }

            switch (c)
            {
                case >= '0' and <= '9':
                case '+' or '-' or '*' or '/' or '(' or ')':
                    builder.Append(c);
                    break;
                case '×':
                    builder.Append('*');
                    break;
                case '÷':
                    builder.Append('/');
                    break;
                case '−':
                    builder.Append('-');
                    break;
                default:
                    return null;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool Matches(string s, int index, string token) =>
        token.Length > 0 && string.CompareOrdinal(s, index, token, 0, token.Length) == 0;

    // Recursive-descent parser: expr := term (('+'|'-') term)*; term := factor (('*'|'/') factor)*;
    // factor := ('+'|'-') factor | number | '(' expr ')'.
    private sealed class Parser(string text)
    {
        private int _position;

        public bool AtEnd => _position >= text.Length;

        public bool TryParseExpression(out decimal value)
        {
            if (!TryParseTerm(out value))
            {
                return false;
            }

            while (!AtEnd && text[_position] is '+' or '-')
            {
                var op = text[_position++];
                if (!TryParseTerm(out var right))
                {
                    return false;
                }

                value = op == '+' ? value + right : value - right;
            }

            return true;
        }

        private bool TryParseTerm(out decimal value)
        {
            if (!TryParseFactor(out value))
            {
                return false;
            }

            while (!AtEnd && text[_position] is '*' or '/')
            {
                var op = text[_position++];
                if (!TryParseFactor(out var right))
                {
                    return false;
                }

                value = op == '*' ? value * right : value / right;
            }

            return true;
        }

        private bool TryParseFactor(out decimal value)
        {
            value = 0;
            if (AtEnd)
            {
                return false;
            }

            var c = text[_position];
            if (c is '+' or '-')
            {
                _position++;
                if (!TryParseFactor(out var inner))
                {
                    return false;
                }

                value = c == '-' ? -inner : inner;
                return true;
            }

            if (c == '(')
            {
                _position++;
                if (!TryParseExpression(out value) || AtEnd || text[_position] != ')')
                {
                    return false;
                }

                _position++;
                return true;
            }

            var start = _position;
            while (!AtEnd && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
            {
                _position++;
            }

            return _position > start
                && decimal.TryParse(text.AsSpan(start, _position - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }
    }
}
