using System.Globalization;
using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Ynab;

/// <summary>
/// A YNAB budget export (the "Budget"/"Plan" CSV of YNAB's Export Budget): <c>Month, Category
/// Group/Category, Category Group, Category, Budgeted (or Assigned), Activity, Available</c>, one row per
/// category and month (<c>Jan 2026</c>). Read into <see cref="ParseResult.BudgetRows"/>; it has no transactions.
/// </summary>
public sealed class YnabBudgetParser : IFileImportParser
{
    private static readonly string[] MonthFormats = ["MMM yyyy", "MMMM yyyy", "yyyy-MM", "MM/yyyy", "M/yyyy", "yyyy-MM-dd", "MMM yy"];

    /// <inheritdoc />
    public bool CanParse(string fileName, ReadOnlySpan<byte> head) =>
        AppExportTable.HeaderHas(head, "Month", "Category Group", "Category", "Activity", "Available")
        && (AppExportTable.HeaderHas(head, "Budgeted") || AppExportTable.HeaderHas(head, "Assigned"));

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

        var month = table.Column("Month");
        var group = table.Column("Category Group");
        var category = table.Column("Category");
        var assigned = table.Column("Budgeted", "Assigned");
        var activity = table.Column("Activity");
        var available = table.Column("Available");
        if (month is null || group is null || category is null || assigned is null)
        {
            warnings.Add(new(ImportWarningCode.InvalidMapping, "The file lacks the Month, Category Group, Category or Budgeted column of a YNAB budget export."));
            return new ParseResult(ImportFileFormat.YnabBudget, [], [], warnings, null, table.EncodingName);
        }

        var currency = Keel.Domain.Currency.Normalize(options.Currency);
        var separator = AmountText.DetectDecimalSeparator(table.Rows.SelectMany(r => new[] { r[assigned], r[activity], r[available] }).Where(v => v.Length > 0));
        var rows = new List<ParsedBudgetRow>();
        foreach (var row in table.Rows)
        {
            if (!DateOnly.TryParseExact(row[month], MonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var first))
            {
                warnings.Add(new(ImportWarningCode.InvalidDate, "Row skipped: the month could not be read.", row.Line));
                continue;
            }

            if (!Amount(row[assigned], out var assignedAmount) || !Optional(row[activity], out var activityAmount) || !Optional(row[available], out var availableAmount))
            {
                warnings.Add(new(ImportWarningCode.InvalidAmount, "Row skipped: an amount could not be read.", row.Line));
                continue;
            }

            if (row[group].Length == 0 || row[category].Length == 0)
            {
                warnings.Add(new(ImportWarningCode.SkippedRow, "Row skipped: it names no category.", row.Line));
                continue;
            }

            rows.Add(new ParsedBudgetRow(new DateOnly(first.Year, first.Month, 1), row[group], row[category], assignedAmount, activityAmount, availableAmount, row.Line));
        }

        if (rows.Count == 0)
        {
            warnings.Add(new(ImportWarningCode.NoTransactions, "The file contains no budget rows."));
        }

        return new ParseResult(ImportFileFormat.YnabBudget, [], [], warnings, null, table.EncodingName) { BudgetRows = rows };

        bool Amount(string text, out long value)
        {
            value = 0;
            return text.Length == 0 || AmountText.TryParseMinor(text, separator, currency, out value);
        }

        bool Optional(string text, out long? value)
        {
            value = null;
            if (text.Length == 0)
            {
                return true;
            }

            if (!AmountText.TryParseMinor(text, separator, currency, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }
    }
}
