using System.Globalization;
using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Csv;

/// <summary>
/// CSV import (F-TXN-2): encoding and delimiter sniffing, header detection after bank
/// preambles, layout auto-detection (signed amount, debit/credit columns, amount plus type) and
/// date-format detection with an explicit ambiguity report. With
/// <see cref="ImportOptions.CsvMapping"/> set, parsing is fully determined by the mapping.
/// </summary>
public sealed class CsvImportParser : IFileImportParser
{
    private static readonly string[] Extensions = [".csv", ".tsv", ".txt"];

    /// <inheritdoc />
    public bool CanParse(string fileName, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var text = TextDecoder.DecodeHead(head).TrimStart();
        if (text.Length == 0 || text[0] is '<' or '!' || text.StartsWith("OFXHEADER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstLine = text.Split('\n')[0];
        return firstLine.IndexOfAny([',', ';', '\t', '|']) >= 0;
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

        DetectedCsvLayout layout;
        List<CsvRow> rows;
        if (options.CsvMapping is { } given)
        {
            rows = CsvRows.Read(decoded.Text, given.Delimiter);
            var headers = HeadersFor(rows, given);
            layout = new DetectedCsvLayout(given, headers, [given.DateFormat], false);
        }
        else
        {
            var delimiter = DelimiterSniffer.Sniff(decoded.Text);
            rows = CsvRows.Read(decoded.Text, delimiter);
            var detection = CsvLayoutDetector.Detect(rows, delimiter, options.PreferredDateOrder);
            warnings.AddRange(detection.Warnings);
            if (detection.Mapping is null)
            {
                return new ParseResult(ImportFileFormat.Csv, [], [], warnings, null, decoded.Encoding.WebName);
            }

            layout = new DetectedCsvLayout(detection.Mapping, detection.Headers, detection.DateFormatCandidates, detection.IsDateFormatAmbiguous);
        }

        var transactions = ParseRows(rows, layout, options, warnings);
        if (transactions.Count == 0)
        {
            warnings.Add(new(ImportWarningCode.NoTransactions, "The file contains no transactions."));
        }

        return new ParseResult(ImportFileFormat.Csv, transactions, [], warnings, layout, decoded.Encoding.WebName);
    }

    private static string[] HeadersFor(List<CsvRow> rows, CsvColumnMapping mapping)
    {
        if (mapping.HasHeader && mapping.SkipRows < rows.Count)
        {
            return rows[mapping.SkipRows].Fields.Select(f => f.Trim()).ToArray();
        }

        var width = rows.Count == 0 ? 0 : rows.Skip(mapping.SkipRows).DefaultIfEmpty(rows[0]).Max(r => r.Fields.Length);
        return Enumerable.Range(1, width).Select(i => string.Create(CultureInfo.InvariantCulture, $"Column {i}")).ToArray();
    }

    private static List<ParsedTransaction> ParseRows(
        List<CsvRow> rows, DetectedCsvLayout layout, ImportOptions options, List<ImportWarning> warnings)
    {
        var m = layout.Mapping;
        var mapped = new HashSet<int?>
        {
            m.DateColumn, m.PayeeColumn, m.MemoColumn, m.AmountColumn, m.DebitColumn, m.CreditColumn, m.TypeColumn,
            m.IdColumn, m.CheckNumberColumn, m.CategoryColumn, m.StatusColumn, m.CurrencyColumn,
        };

        var transactions = new List<ParsedTransaction>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var first = m.SkipRows + (m.HasHeader ? 1 : 0);
        for (var i = first; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.IsBlank)
            {
                continue;
            }

            if (!DateText.TryParse(row[m.DateColumn], m.DateFormat, out var date))
            {
                warnings.Add(new(ImportWarningCode.InvalidDate, "Row skipped: the date could not be read.", row.Line));
                continue;
            }

            var currency = Keel.Domain.Currency.IsValidCode(row[m.CurrencyColumn].ToUpperInvariant())
                ? row[m.CurrencyColumn].ToUpperInvariant()
                : Keel.Domain.Currency.Normalize(options.Currency);
            if (ReadAmount(row, m, currency, warnings) is not { } amount)
            {
                continue;
            }

            var id = NullIfEmpty(row[m.IdColumn]);
            if (id is not null && !seenIds.Add(id))
            {
                warnings.Add(new(ImportWarningCode.DuplicateProviderId, "The transaction id appears more than once.", row.Line));
            }

            var extras = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < row.Fields.Length; c++)
            {
                if (!mapped.Contains(c) && row[c].Length > 0)
                {
                    var name = c < layout.Headers.Count && layout.Headers[c].Length > 0
                        ? layout.Headers[c]
                        : string.Create(CultureInfo.InvariantCulture, $"Column {c + 1}");
                    extras.TryAdd(name, row[c]);
                }
            }

            transactions.Add(new ParsedTransaction
            {
                Date = date,
                Amount = amount,
                PayeeRaw = row[m.PayeeColumn],
                Memo = NullIfEmpty(row[m.MemoColumn]),
                ProviderTransactionId = id,
                IsPending = m.StatusColumn is not null && CsvVocabulary.IsPendingStatus(row[m.StatusColumn]),
                CheckNumber = NullIfEmpty(row[m.CheckNumberColumn]),
                Currency = currency,
                TransactionType = NullIfEmpty(row[m.TypeColumn]),
                Category = NullIfEmpty(row[m.CategoryColumn]),
                Extras = extras,
                SourceIndex = transactions.Count,
                SourceLine = row.Line,
            });
        }

        return transactions;
    }

    private static long? ReadAmount(CsvRow row, CsvColumnMapping m, string currency, List<ImportWarning> warnings)
    {
        switch (m.AmountLayout)
        {
            case CsvAmountLayout.DebitCredit:
                {
                    var debitText = row[m.DebitColumn];
                    var creditText = row[m.CreditColumn];
                    if (debitText.Length == 0 && creditText.Length == 0)
                    {
                        warnings.Add(new(ImportWarningCode.SkippedRow, "Row skipped: it has no amount.", row.Line));
                        return null;
                    }

                    long debit = 0, credit = 0;
                    if ((debitText.Length > 0 && !AmountText.TryParseMinor(debitText, m.DecimalSeparator, currency, out debit))
                        || (creditText.Length > 0 && !AmountText.TryParseMinor(creditText, m.DecimalSeparator, currency, out credit)))
                    {
                        warnings.Add(new(ImportWarningCode.InvalidAmount, "Row skipped: the amount could not be read.", row.Line));
                        return null;
                    }

                    return Math.Abs(credit) - Math.Abs(debit);
                }

            case CsvAmountLayout.AmountWithType:
            case CsvAmountLayout.SignedAmount:
            default:
                {
                    var text = row[m.AmountColumn];
                    if (text.Length == 0)
                    {
                        warnings.Add(new(ImportWarningCode.SkippedRow, "Row skipped: it has no amount.", row.Line));
                        return null;
                    }

                    if (!AmountText.TryParseMinor(text, m.DecimalSeparator, currency, out var amount))
                    {
                        warnings.Add(new(ImportWarningCode.InvalidAmount, "Row skipped: the amount could not be read.", row.Line));
                        return null;
                    }

                    if (m.AmountLayout == CsvAmountLayout.AmountWithType)
                    {
                        switch (CsvVocabulary.ClassifyType(row[m.TypeColumn]))
                        {
                            case TypeDirection.Outflow:
                                return -Math.Abs(amount);
                            case TypeDirection.Inflow:
                                return Math.Abs(amount);
                            default:
                                warnings.Add(new(ImportWarningCode.UnknownTransactionType,
                                    "The transaction type is neither a debit nor a credit; the amount's sign was kept.", row.Line));
                                break;
                        }
                    }

                    return m.SignConvention == CsvSignConvention.OutflowPositive ? -amount : amount;
                }
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
