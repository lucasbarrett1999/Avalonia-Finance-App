using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace Keel.Infrastructure.Import.Csv;

/// <summary>One CSV record with the one-based line where it starts.</summary>
internal sealed record CsvRow(int Line, string[] Fields)
{
    /// <summary>The trimmed field, or an empty string past the end of the row.</summary>
    public string this[int? column] =>
        column is { } c && c >= 0 && c < Fields.Length ? Fields[c].Trim() : string.Empty;

    /// <summary>True when every field is blank.</summary>
    public bool IsBlank => Fields.All(string.IsNullOrWhiteSpace);
}

/// <summary>Reads CSV text into records with CsvHelper (RFC 4180 quoting, tolerant of bad data).</summary>
internal static class CsvRows
{
    /// <summary>Reads every non-blank line. Blank lines are not records, so they do not count
    /// towards <c>SkipRows</c>.</summary>
    public static List<CsvRow> Read(string text, char delimiter)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
            IgnoreBlankLines = true,
            Mode = CsvMode.RFC4180,
        };

        var rows = new List<CsvRow>();
        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, config);
        while (parser.Read())
        {
            var record = parser.Record ?? [];
            var raw = parser.RawRecord.TrimEnd('\r', '\n');
            var innerLineBreaks = raw.Count(c => c == '\n');
            rows.Add(new CsvRow(parser.RawRow - innerLineBreaks, record));
        }

        return rows;
    }
}

/// <summary>Picks the field delimiter by how consistently each candidate splits the first lines.</summary>
internal static class DelimiterSniffer
{
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>The delimiter with the best consistency × field count over the first 50 non-blank lines;
    /// a comma when nothing splits.</summary>
    public static char Sniff(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Take(50).ToList();
        var best = ',';
        var bestScore = 0.0;
        foreach (var candidate in Candidates)
        {
            var counts = lines.Select(l => CountFields(l, candidate)).ToList();
            if (counts.Count == 0)
            {
                continue;
            }

            var modal = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (modal.Key < 2)
            {
                continue;
            }

            var score = (double)modal.Count() / counts.Count * modal.Key;
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static int CountFields(string line, char delimiter)
    {
        var count = 1;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == delimiter && !quoted)
            {
                count++;
            }
        }

        return count;
    }
}
