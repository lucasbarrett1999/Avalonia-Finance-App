using System.Globalization;
using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Qif;

/// <summary>
/// QIF import (F-TXN-2) for the non-investment account types <c>!Type:Bank</c>,
/// <c>!Type:CCard</c>, <c>!Type:Cash</c>, <c>!Type:Oth A</c> and <c>!Type:Oth L</c>, including
/// multi-account exports with <c>!Account</c> blocks. Record fields: <c>D</c> date (<c>1/ 2'26</c>,
/// <c>01/02/2026</c>, <c>2026-01-02</c>), <c>T</c>/<c>U</c> amount, <c>P</c> payee, <c>M</c> memo,
/// <c>N</c> number, <c>C</c> cleared, <c>L</c> category, <c>A</c> address, <c>S</c>/<c>E</c>/<c>$</c>
/// splits, <c>^</c> end. Amounts keep the file's sign.
/// </summary>
public sealed class QifImportParser : IFileImportParser
{
    private static readonly Dictionary<string, string> TransactionSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bank"] = "Bank",
        ["CCard"] = "CCard",
        ["Cash"] = "Cash",
        ["Oth A"] = "Oth A",
        ["Oth L"] = "Oth L",
    };

    private static readonly HashSet<string> SilentSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cat", "Class", "Memorized", "Prices", "Security", "Template",
    };

    /// <inheritdoc />
    public bool CanParse(string fileName, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.EndsWith(".qif", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = TextDecoder.DecodeHead(head).TrimStart();
        return text.StartsWith("!Type:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("!Account", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("!Option:", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<ParseResult> ParseAsync(Stream stream, ImportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return Parse(buffer.ToArray(), options);
    }

    /// <summary>Synchronous core of <see cref="ParseAsync"/>.</summary>
    public static ParseResult Parse(ReadOnlySpan<byte> bytes, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var warnings = new List<ImportWarning>();
        var decoded = TextDecoder.Decode(bytes, options.EncodingName);
        if (decoded.UsedFallback)
        {
            warnings.Add(new(ImportWarningCode.EncodingFallback, "The file is not valid UTF-8; decoded as Windows-1252."));
        }

        var currency = Keel.Domain.Currency.Normalize(options.Currency);
        var records = ReadRecords(decoded.Text, warnings, out var accounts);
        var order = ChooseDateOrder(records, options.PreferredDateOrder, warnings);

        var transactions = new List<ParsedTransaction>();
        foreach (var record in records)
        {
            if (Build(record, order, currency, warnings) is { } parsed)
            {
                transactions.Add(parsed with { SourceIndex = transactions.Count });
            }
        }

        if (transactions.Count == 0)
        {
            warnings.Add(new(ImportWarningCode.NoTransactions, "The file contains no transactions."));
        }

        return new ParseResult(ImportFileFormat.Qif, transactions, accounts, warnings, null, decoded.Encoding.WebName);
    }

    private sealed class QifRecord(int line, string? accountName)
    {
        public int Line { get; } = line;

        public string? AccountName { get; } = accountName;

        public List<(char Code, string Value)> Fields { get; } = [];
    }

    private static List<QifRecord> ReadRecords(string text, List<ImportWarning> warnings, out List<DetectedAccount> accounts)
    {
        var found = new List<DetectedAccount>();
        accounts = found;
        var records = new List<QifRecord>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        string? section = null;
        var inAccountBlock = false;
        var accountFields = new Dictionary<char, string>();
        string? accountName = null;
        QifRecord? current = null;
        var sectionAnnounced = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var lineNumber = i + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '!')
            {
                FlushRecord();
                var directive = line[1..].Trim();
                if (directive.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                {
                    var type = directive[5..].Trim();
                    inAccountBlock = false;
                    sectionAnnounced = false;
                    section = TransactionSections.TryGetValue(type, out var known) ? known : null;
                    if (section is not null)
                    {
                        AnnounceAccount(section);
                    }
                    else if (!SilentSections.Contains(type))
                    {
                        warnings.Add(new(ImportWarningCode.UnsupportedSection,
                            string.Create(CultureInfo.InvariantCulture, $"The !Type:{type} section was ignored."), lineNumber));
                    }
                }
                else if (directive.StartsWith("Account", StringComparison.OrdinalIgnoreCase))
                {
                    inAccountBlock = true;
                    accountFields.Clear();
                }

                // !Option:AutoSwitch and !Clear:AutoSwitch only toggle Quicken's list mode.
                continue;
            }

            if (inAccountBlock)
            {
                if (line[0] == '^')
                {
                    accountName = accountFields.GetValueOrDefault('N');
                    sectionAnnounced = false;
                }
                else
                {
                    accountFields[line[0]] = line[1..].Trim();
                }

                continue;
            }

            if (section is null)
            {
                continue;
            }

            if (line[0] == '^')
            {
                FlushRecord();
                continue;
            }

            current ??= new QifRecord(lineNumber, accountName);
            current.Fields.Add((line[0], line[1..]));
        }

        if (current is { Fields.Count: > 0 })
        {
            warnings.Add(new(ImportWarningCode.MalformedMarkup, "The last record has no closing ^.", current.Line));
            FlushRecord();
        }

        return records;

        void FlushRecord()
        {
            if (current is { Fields.Count: > 0 })
            {
                records.Add(current);
            }

            current = null;
        }

        void AnnounceAccount(string type)
        {
            if (sectionAnnounced)
            {
                return;
            }

            sectionAnnounced = true;
            found.Add(new DetectedAccount { Name = accountName, AccountId = accountName, AccountType = type });
        }
    }

    private static DateOrder ChooseDateOrder(List<QifRecord> records, DateOrder? preferred, List<ImportWarning> warnings)
    {
        var monthFirstPossible = true;
        var dayFirstPossible = true;
        var matters = false;
        foreach (var record in records)
        {
            foreach (var (code, value) in record.Fields)
            {
                if (code != 'D' || !TrySplitDate(value, out var a, out var b, out _, out var yearFirst) || yearFirst)
                {
                    continue;
                }

                monthFirstPossible &= a <= 12 && b <= 31;
                dayFirstPossible &= b <= 12 && a <= 31;
                matters |= a != b;
            }
        }

        if (monthFirstPossible && dayFirstPossible && matters)
        {
            if (preferred is { } answer)
            {
                return answer;
            }

            warnings.Add(new(ImportWarningCode.AmbiguousDateFormat,
                "Dates fit both month-first and day-first; assumed month-first.", null, ["MM/dd/yyyy", "dd/MM/yyyy"]));
            return DateOrder.MonthFirst;
        }

        return !monthFirstPossible && dayFirstPossible ? DateOrder.DayFirst : DateOrder.MonthFirst;
    }

    /// <summary>
    /// Splits a QIF date into its parts. Quicken writes <c>M/D'YY</c> for 2000 and later
    /// (<c>1/ 2'26</c>, <c>12/31' 5</c>) and <c>M/D/YY</c> before; exports also use four-digit
    /// years, dashes and dots, and ISO <c>yyyy-MM-dd</c>.
    /// </summary>
    internal static bool TrySplitDate(string value, out int first, out int second, out int year, out bool yearFirst)
    {
        first = second = year = 0;
        yearFirst = false;
        var v = value.Trim();
        var apostrophe = v.Contains('\'');
        var parts = v.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Split(['/', '-', '.', '\''], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts.All(p => p.All(char.IsAsciiDigit)) || parts.Any(p => p.Length > 4))
        {
            return false;
        }

        var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (parts[0].Length == 4)
        {
            yearFirst = true;
            (year, first, second) = (numbers[0], numbers[1], numbers[2]);
            return true;
        }

        (first, second) = (numbers[0], numbers[1]);
        year = parts[2].Length switch
        {
            4 => numbers[2],
            _ when apostrophe => 2000 + numbers[2],
            _ => numbers[2] < 50 ? 2000 + numbers[2] : 1900 + numbers[2],
        };
        return true;
    }

    private static DateOnly? ParseDate(string value, DateOrder order)
    {
        if (!TrySplitDate(value, out var a, out var b, out var year, out var yearFirst))
        {
            return null;
        }

        var (month, day) = yearFirst || order == DateOrder.MonthFirst ? (a, b) : (b, a);
        return month is >= 1 and <= 12 && year is >= 1 and <= 9999 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    private static ParsedTransaction? Build(QifRecord record, DateOrder order, string currency, List<ImportWarning> warnings)
    {
        string? Field(char code) => record.Fields.Where(f => f.Code == code).Select(f => f.Value.Trim()).FirstOrDefault();

        var dateText = Field('D');
        if (dateText is null || ParseDate(dateText, order) is not { } date)
        {
            warnings.Add(new(ImportWarningCode.InvalidDate, "Record skipped: the date could not be read.", record.Line));
            return null;
        }

        var amountText = Field('T') ?? Field('U');
        if (!AmountText.TryParseMinor(amountText, AmountText.AutoSeparator, currency, out var amount))
        {
            warnings.Add(new(ImportWarningCode.InvalidAmount, "Record skipped: the amount could not be read.", record.Line));
            return null;
        }

        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        var splits = new List<ParsedSplit>();
        var address = new List<string>();
        (string? Category, string? Memo, long Amount)? split = null;
        foreach (var (code, raw) in record.Fields)
        {
            var value = raw.Trim();
            switch (code)
            {
                case 'D' or 'T' or 'U' or 'P' or 'M' or 'N' or 'L':
                    break;
                case 'C':
                    extras["Cleared"] = value;
                    break;
                case 'A':
                    address.Add(value);
                    break;
                case 'S':
                    FlushSplit();
                    split = (NullIfEmpty(value), null, 0);
                    break;
                case 'E':
                    split = (split?.Category, NullIfEmpty(value), split?.Amount ?? 0);
                    break;
                case '$':
                    if (!AmountText.TryParseMinor(value, AmountText.AutoSeparator, currency, out var splitAmount))
                    {
                        warnings.Add(new(ImportWarningCode.InvalidAmount, "A split amount could not be read; it was set to zero.", record.Line));
                    }

                    split = (split?.Category, split?.Memo, splitAmount);
                    break;
                default:
                    extras.TryAdd(code.ToString(), value);
                    break;
            }
        }

        FlushSplit();
        if (address.Count > 0)
        {
            extras["Address"] = string.Join('\n', address);
        }

        var category = NullIfEmpty(Field('L'));
        if (category is ['[', .., ']'])
        {
            extras["TransferAccount"] = category[1..^1];
        }

        return new ParsedTransaction
        {
            Date = date,
            Amount = amount,
            PayeeRaw = Field('P') ?? string.Empty,
            Memo = NullIfEmpty(Field('M')),
            CheckNumber = NullIfEmpty(Field('N')),
            Currency = currency,
            Category = category,
            SourceAccountId = record.AccountName,
            Splits = splits,
            Extras = extras,
            SourceLine = record.Line,
        };

        void FlushSplit()
        {
            if (split is { } s)
            {
                splits.Add(new ParsedSplit(s.Category, s.Memo, s.Amount));
            }

            split = null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
