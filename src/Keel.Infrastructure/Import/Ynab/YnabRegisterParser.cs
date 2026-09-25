using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Ynab;

/// <summary>
/// A YNAB register export (the "Register" CSV of YNAB's Export Budget): <c>Account, Flag, Date, Payee,
/// Category Group/Category, Category Group, Category, Memo, Outflow, Inflow, Cleared</c>. Every account is
/// in one file, so each row carries its account name (<see cref="ParsedTransaction.SourceAccountId"/>);
/// the amount is Inflow minus Outflow; the category is <c>Group: Category</c> with the parts in
/// <see cref="ParsedTransaction.Extras"/> (<c>Category Group</c>, <c>Category</c>), as are <c>Flag</c> and
/// <c>Cleared</c>. The date format is YNAB's per-budget setting and is detected from the whole column.
/// Recognized by its header only, ahead of the generic CSV layout detection.
/// </summary>
public sealed class YnabRegisterParser : IFileImportParser
{
    /// <summary>The header columns that identify the export.</summary>
    public static readonly string[] Signature = ["Account", "Flag", "Date", "Payee", "Outflow", "Inflow", "Cleared"];

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

        var account = table.Column("Account");
        var flag = table.Column("Flag");
        var date = table.Column("Date");
        var payee = table.Column("Payee");
        var path = table.Column("Category Group/Category");
        var group = table.Column("Category Group");
        var category = table.Column("Category");
        var memo = table.Column("Memo");
        var outflow = table.Column("Outflow");
        var inflow = table.Column("Inflow");
        var cleared = table.Column("Cleared");
        if (account is null || date is null || outflow is null || inflow is null)
        {
            warnings.Add(new(ImportWarningCode.InvalidMapping, "The file lacks the Account, Date, Outflow or Inflow column of a YNAB register export."));
            return new ParseResult(ImportFileFormat.Ynab, [], [], warnings, null, table.EncodingName);
        }

        var currency = Keel.Domain.Currency.Normalize(options.Currency);
        var format = table.DetectDateFormat(date, options.PreferredDateOrder, warnings);
        var separator = AmountText.DetectDecimalSeparator(table.Rows.SelectMany(r => new[] { r[outflow], r[inflow] }).Where(v => v.Length > 0));
        var mapped = new HashSet<int> { account.Value, date.Value, outflow.Value, inflow.Value };
        foreach (var c in new[] { payee, path, memo })
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

            long spent = 0, received = 0;
            if ((row[outflow].Length > 0 && !AmountText.TryParseMinor(row[outflow], separator, currency, out spent))
                || (row[inflow].Length > 0 && !AmountText.TryParseMinor(row[inflow], separator, currency, out received)))
            {
                warnings.Add(new(ImportWarningCode.InvalidAmount, "Row skipped: the amount could not be read.", row.Line));
                continue;
            }

            var groupName = row[group];
            var categoryName = row[category];
            var categoryPath = row[path].Length > 0 ? row[path]
                : groupName.Length > 0 || categoryName.Length > 0 ? $"{groupName}: {categoryName}" : string.Empty;
            transactions.Add(new ParsedTransaction
            {
                Date = day,
                Amount = Math.Abs(received) - Math.Abs(spent),
                PayeeRaw = row[payee],
                Memo = row[memo].Length == 0 ? null : row[memo],
                Currency = currency,
                Category = categoryPath.Length == 0 ? null : categoryPath,
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

        return new ParseResult(ImportFileFormat.Ynab, transactions, AppExportTable.AccountsOf(transactions), warnings, null, table.EncodingName);
    }
}
