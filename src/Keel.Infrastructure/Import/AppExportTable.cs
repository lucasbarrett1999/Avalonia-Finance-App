using Keel.Application.Import;
using Keel.Infrastructure.Import.Csv;

namespace Keel.Infrastructure.Import;

/// <summary>
/// A CSV exported by another budgeting app (YNAB, Monarch): a fixed header row naming the columns, then one
/// record per row. Columns are found by header name (case and surrounding spaces ignored), so the apps may
/// add, drop or reorder optional columns. Shared by the YNAB and Monarch parsers.
/// </summary>
internal sealed class AppExportTable
{
    private readonly Dictionary<string, int> _columns;

    private AppExportTable(List<CsvRow> rows, Dictionary<string, int> columns, string[] headers, string encodingName, bool usedFallback)
    {
        Rows = rows;
        _columns = columns;
        Headers = headers;
        EncodingName = encodingName;
        UsedFallback = usedFallback;
    }

    /// <summary>Data rows (after the header), blank lines excluded.</summary>
    public List<CsvRow> Rows { get; }

    /// <summary>Header names as the file spells them.</summary>
    public string[] Headers { get; }

    /// <summary>Web name of the text encoding.</summary>
    public string EncodingName { get; }

    /// <summary>Whether the bytes were not valid UTF-8 and Windows-1252 was used.</summary>
    public bool UsedFallback { get; }

    /// <summary>Whether the file's first line names every column in <paramref name="required"/>.</summary>
    public static bool HeaderHas(ReadOnlySpan<byte> head, params string[] required)
    {
        var text = TextDecoder.DecodeHead(head);
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = (end < 0 ? text : text[..end]).TrimEnd('\r');
        if (firstLine.Length == 0)
        {
            return false;
        }

        var rows = CsvRows.Read(firstLine, DelimiterSniffer.Sniff(firstLine));
        if (rows.Count == 0)
        {
            return false;
        }

        var names = rows[0].Fields.Select(Key).ToHashSet(StringComparer.Ordinal);
        return required.All(r => names.Contains(Key(r)));
    }

    /// <summary>Decodes and reads the whole file.</summary>
    public static AppExportTable Read(ReadOnlySpan<byte> bytes, ImportOptions options)
    {
        var decoded = TextDecoder.Decode(bytes, options.EncodingName);
        var delimiter = DelimiterSniffer.Sniff(decoded.Text);
        var rows = CsvRows.Read(decoded.Text, delimiter);
        var headers = rows.Count == 0 ? [] : rows[0].Fields.Select(f => f.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Length; i++)
        {
            columns.TryAdd(Key(headers[i]), i);
        }

        return new AppExportTable(rows.Skip(1).Where(r => !r.IsBlank).ToList(), columns, headers, decoded.Encoding.WebName, decoded.UsedFallback);
    }

    /// <summary>The index of the first of <paramref name="names"/> present, or null.</summary>
    public int? Column(params string[] names)
    {
        foreach (var name in names)
        {
            if (_columns.TryGetValue(Key(name), out var index))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the date format of a column over every row (YNAB writes the user's chosen format), adding an
    /// ambiguity warning with both candidates when day and month cannot be told apart.
    /// </summary>
    public string? DetectDateFormat(int? column, DateOrder? preferred, List<ImportWarning> warnings)
    {
        var detection = DateText.Detect(Rows.Select(r => r[column]).ToList(), preferred);
        if (detection.IsAmbiguous)
        {
            warnings.Add(new(ImportWarningCode.AmbiguousDateFormat, "Day and month order cannot be determined; month first was assumed.",
                Candidates: detection.Candidates.Where(c => c.Order is not null).Select(c => c.Name).Distinct().ToList()));
        }

        return detection.Best?.Name;
    }

    /// <summary>The account names of the file in order of first appearance.</summary>
    public static IReadOnlyList<DetectedAccount> AccountsOf(IEnumerable<ParsedTransaction> rows) =>
        rows.Select(r => r.SourceAccountId).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(name => new DetectedAccount { AccountId = name, Name = name })
            .ToList();

    /// <summary>Every non-empty field of <paramref name="row"/> whose column is not in <paramref name="mapped"/>, by header name.</summary>
    public Dictionary<string, string> Extras(CsvRow row, IReadOnlySet<int> mapped)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var c = 0; c < row.Fields.Length && c < Headers.Length; c++)
        {
            if (!mapped.Contains(c) && row[c].Length > 0 && Headers[c].Length > 0)
            {
                extras.TryAdd(Headers[c], row[c]);
            }
        }

        return extras;
    }

    private static string Key(string header) => string.Join(' ', header.Trim().Trim('﻿').Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
