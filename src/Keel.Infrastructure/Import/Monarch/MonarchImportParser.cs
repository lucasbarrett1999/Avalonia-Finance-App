using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Monarch;

/// <summary>
/// A Monarch transactions export: <c>Date, Merchant, Category, Account, Original Statement, Notes, Amount,
/// Tags</c>. Every account is in one file (<see cref="ParsedTransaction.SourceAccountId"/>); the amount is
/// signed with outflows negative; the payee is the merchant (the original statement when there is none);
/// <c>Original Statement</c> and <c>Tags</c> (comma-separated) stay in <see cref="ParsedTransaction.Extras"/>.
/// Recognized by its header only, ahead of the generic CSV layout detection.
/// </summary>
public sealed class MonarchImportParser : IFileImportParser
{
    /// <summary>The header columns that identify the export.</summary>
    public static readonly string[] Signature = ["Date", "Merchant", "Category", "Account", "Original Statement", "Amount"];

    /// <inheritdoc />
    public bool CanParse(string fileName, ReadOnlySpan<byte> head) => AppExportTable.HeaderHas(head, Signature);

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
        var table = AppExportTable.Read(bytes, options);
        if (table.UsedFallback)
        {
            warnings.Add(new(ImportWarningCode.EncodingFallback, "The file is not valid UTF-8; decoded as Windows-1252."));
        }

        var date = table.Column("Date");
        var merchant = table.Column("Merchant");
        var category = table.Column("Category");
        var account = table.Column("Account");
        var statement = table.Column("Original Statement");
        var notes = table.Column("Notes");
        var amount = table.Column("Amount");
        if (date is null || account is null || amount is null)
        {
            warnings.Add(new(ImportWarningCode.InvalidMapping, "The file lacks the Date, Account or Amount column of a Monarch export."));
            return new ParseResult(ImportFileFormat.Monarch, [], [], warnings, null, table.EncodingName);
        }

        var currency = Keel.Domain.Currency.Normalize(options.Currency);
        var format = table.DetectDateFormat(date, options.PreferredDateOrder, warnings);
        var separator = AmountText.DetectDecimalSeparator(table.Rows.Select(r => r[amount]).Where(v => v.Length > 0));
        var mapped = new HashSet<int> { date.Value, account.Value, amount.Value };
        foreach (var c in new[] { merchant, category, notes })
        {
            if (c is { } index)
            {
                mapped.Add(index);
            }
        }

        var transactions = new List<ParsedTransaction>();
        foreach (var row in table.Rows)
        {
            if (format is null || !DateText.TryParse(row[date], format, out var day))
            {
                warnings.Add(new(ImportWarningCode.InvalidDate, "Row skipped: the date could not be read.", row.Line));
                continue;
            }

            if (row[amount].Length == 0)
            {
                warnings.Add(new(ImportWarningCode.SkippedRow, "Row skipped: it has no amount.", row.Line));
                continue;
            }

            if (!AmountText.TryParseMinor(row[amount], separator, currency, out var value))
            {
                warnings.Add(new(ImportWarningCode.InvalidAmount, "Row skipped: the amount could not be read.", row.Line));
                continue;
            }

            transactions.Add(new ParsedTransaction
            {
                Date = day,
                Amount = value,
                PayeeRaw = row[merchant].Length > 0 ? row[merchant] : row[statement],
                Memo = row[notes].Length == 0 ? null : row[notes],
                Currency = currency,
                Category = row[category].Length == 0 ? null : row[category],
                SourceAccountId = row[account],
                Extras = table.Extras(row, mapped),
                SourceIndex = transactions.Count,
                SourceLine = row.Line,
            });
        }

        if (transactions.Count == 0)
        {
            warnings.Add(new(ImportWarningCode.NoTransactions, "The file contains no transactions."));
        }

        return new ParseResult(ImportFileFormat.Monarch, transactions, AppExportTable.AccountsOf(transactions), warnings, null, table.EncodingName);
    }

    /// <summary>The tag names of a Monarch row's <c>Tags</c> field (comma-separated).</summary>
    public static IReadOnlyList<string> TagsOf(ParsedTransaction row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Extras.TryGetValue("Tags", out var tags)
            ? tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }
}
