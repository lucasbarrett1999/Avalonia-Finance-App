using System.Globalization;
using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Csv;

/// <summary>Outcome of CSV layout detection.</summary>
internal sealed record CsvDetection(
    CsvColumnMapping? Mapping,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> DateFormatCandidates,
    bool IsDateFormatAmbiguous,
    IReadOnlyList<ImportWarning> Warnings);

/// <summary>
/// Detects a <see cref="CsvColumnMapping"/> from the records of a file: the header row (after
/// any bank preamble), each column's role, the date format, the decimal separator, the amount
/// layout and the sign convention.
/// </summary>
internal static class CsvLayoutDetector
{
    private const int HeaderSearchRows = 50;

    private static readonly string[] PaymentWords = ["PAYMENT", "THANK YOU", "AUTOPAY", "AUTO PAY"];

    /// <summary>Detects the layout. Returns a null mapping (with a warning) when no date or amount column exists.</summary>
    public static CsvDetection Detect(IReadOnlyList<CsvRow> rows, char delimiter, DateOrder? preferredOrder)
    {
        var warnings = new List<ImportWarning>();
        var modal = rows.Count == 0 ? 0 : rows.GroupBy(r => r.Fields.Length)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

        var headerIndex = FindHeader(rows);
        int skipRows;
        string[] headers;
        Dictionary<CsvRole, int> roles;
        List<CsvRow> data;
        if (headerIndex >= 0)
        {
            skipRows = headerIndex;
            headers = rows[headerIndex].Fields.Select(f => f.Trim()).ToArray();
            data = rows.Skip(headerIndex + 1).Where(r => !r.IsBlank).ToList();
            roles = AssignByHeader(headers);
        }
        else
        {
            skipRows = Math.Max(0, FindFirstDataRow(rows, modal));
            data = rows.Skip(skipRows).Where(r => !r.IsBlank).ToList();
            var width = data.Count == 0 ? modal : data.Max(r => r.Fields.Length);
            headers = Enumerable.Range(1, width).Select(i => string.Create(CultureInfo.InvariantCulture, $"Column {i}")).ToArray();
            roles = [];
        }

        var width2 = Math.Max(headers.Length, data.Count == 0 ? 0 : data.Max(r => r.Fields.Length));
        if (!roles.ContainsKey(CsvRole.Date) && FindDateColumn(data, width2, roles) is { } dateColumn)
        {
            roles[CsvRole.Date] = dateColumn;
        }

        if (!roles.ContainsKey(CsvRole.Amount) && !roles.ContainsKey(CsvRole.Debit) && !roles.ContainsKey(CsvRole.Credit)
            && FindAmountColumn(data, width2, roles) is { } amountColumn)
        {
            roles[CsvRole.Amount] = amountColumn;
        }

        if (!roles.ContainsKey(CsvRole.Payee) && FindTextColumn(data, width2, roles) is { } payeeColumn)
        {
            roles[CsvRole.Payee] = payeeColumn;
        }

        if (!roles.TryGetValue(CsvRole.Date, out var dateCol))
        {
            warnings.Add(new(ImportWarningCode.InvalidMapping, "No date column was found."));
            return new CsvDetection(null, headers, [], false, warnings);
        }

        var hasAmount = roles.ContainsKey(CsvRole.Amount) || (roles.ContainsKey(CsvRole.Debit) && roles.ContainsKey(CsvRole.Credit));
        if (!hasAmount)
        {
            warnings.Add(new(ImportWarningCode.InvalidMapping, "No amount column was found."));
            return new CsvDetection(null, headers, [], false, warnings);
        }

        var dateValues = data.Select(r => r[dateCol]).Where(DateText.LooksLikeDate).ToList();
        var dates = DateText.Detect(dateValues, preferredOrder);
        if (dates.Best is null)
        {
            warnings.Add(new(ImportWarningCode.InvalidMapping, "The date column does not use one consistent date format."));
            return new CsvDetection(null, headers, [], false, warnings);
        }

        var candidates = dates.Candidates.Select(c => c.Name).ToList();
        if (dates.IsAmbiguous)
        {
            warnings.Add(new(ImportWarningCode.AmbiguousDateFormat,
                "Dates fit both month-first and day-first; assumed month-first.", null, candidates));
        }

        int? Role(CsvRole role) => roles.TryGetValue(role, out var c) ? c : null;
        var amountColumns = new[] { Role(CsvRole.Amount), Role(CsvRole.Debit), Role(CsvRole.Credit) }.OfType<int>();
        var decimalSeparator = AmountText.DetectDecimalSeparator(
            data.SelectMany(r => amountColumns.Select(c => r[c])).Where(v => v.Length > 0));

        var mapping = new CsvColumnMapping
        {
            Delimiter = delimiter,
            SkipRows = skipRows,
            HasHeader = headerIndex >= 0,
            DateColumn = dateCol,
            DateFormat = dates.Best.Name,
            PayeeColumn = Role(CsvRole.Payee),
            MemoColumn = Role(CsvRole.Memo),
            TypeColumn = Role(CsvRole.Type),
            IdColumn = Role(CsvRole.Id),
            CheckNumberColumn = Role(CsvRole.CheckNumber),
            CategoryColumn = Role(CsvRole.Category),
            StatusColumn = Role(CsvRole.Status),
            CurrencyColumn = Role(CsvRole.Currency),
            DecimalSeparator = decimalSeparator,
        };

        mapping = ChooseAmountLayout(mapping, roles, data, warnings);
        return new CsvDetection(mapping, headers, candidates, dates.IsAmbiguous, warnings);
    }

    private static CsvColumnMapping ChooseAmountLayout(
        CsvColumnMapping mapping, Dictionary<CsvRole, int> roles, List<CsvRow> data, List<ImportWarning> warnings)
    {
        if (!roles.TryGetValue(CsvRole.Amount, out var amountCol))
        {
            return mapping with
            {
                AmountLayout = CsvAmountLayout.DebitCredit,
                DebitColumn = roles[CsvRole.Debit],
                CreditColumn = roles[CsvRole.Credit],
            };
        }

        var amounts = data
            .Select(r => (Row: r, Ok: AmountText.TryParse(r[amountCol], mapping.DecimalSeparator, out var v), Value: v))
            .Where(a => a.Ok && a.Value != 0)
            .ToList();
        var negatives = amounts.Count(a => a.Value < 0);
        var positives = amounts.Count - negatives;

        if (negatives == 0 && mapping.TypeColumn is { } typeCol)
        {
            var types = data.Select(r => r[typeCol]).Where(t => t.Length > 0).ToList();
            var known = types.Count(t => CsvVocabulary.ClassifyType(t) != TypeDirection.Unknown);
            if (types.Count > 0 && known * 10 >= types.Count * 8)
            {
                return mapping with { AmountLayout = CsvAmountLayout.AmountWithType, AmountColumn = amountCol };
            }
        }

        mapping = mapping with { AmountLayout = CsvAmountLayout.SignedAmount, AmountColumn = amountCol };
        if (mapping.PayeeColumn is not { } payeeCol || amounts.Count == 0)
        {
            return mapping;
        }

        var paymentRows = amounts
            .Where(a => PaymentWords.Any(w => a.Row[payeeCol].Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (positives > negatives && paymentRows.Count > 0 && paymentRows.All(a => a.Value < 0))
        {
            warnings.Add(new(ImportWarningCode.SignConventionGuessed,
                "Purchases appear positive and payments negative; amounts were read as outflow-positive."));
            return mapping with { SignConvention = CsvSignConvention.OutflowPositive };
        }

        if (negatives == 0)
        {
            warnings.Add(new(ImportWarningCode.SignConventionGuessed,
                "Every amount is positive; check whether they are inflows or outflows."));
        }

        return mapping;
    }

    private static int FindHeader(IReadOnlyList<CsvRow> rows)
    {
        for (var i = 0; i < Math.Min(rows.Count, HeaderSearchRows); i++)
        {
            var fields = rows[i].Fields;
            if (fields.Length < 2 || fields.Any(DateText.LooksLikeDate))
            {
                continue;
            }

            if (fields.Count(CsvVocabulary.IsKnownHeader) >= 2)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindFirstDataRow(IReadOnlyList<CsvRow> rows, int modal)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Fields.Length == modal && rows[i].Fields.Any(DateText.LooksLikeDate))
            {
                return i;
            }
        }

        return 0;
    }

    private static Dictionary<CsvRole, int> AssignByHeader(string[] headers)
    {
        var normalized = headers.Select(CsvVocabulary.NormalizeHeader).ToArray();
        var roles = new Dictionary<CsvRole, int>();
        var claimed = new HashSet<int>();
        foreach (var (role, names) in CsvVocabulary.Headers)
        {
            foreach (var name in names)
            {
                var column = Array.FindIndex(normalized, h => h == name);
                while (column >= 0 && claimed.Contains(column))
                {
                    column = Array.FindIndex(normalized, column + 1, h => h == name);
                }

                if (column >= 0)
                {
                    roles[role] = column;
                    claimed.Add(column);
                    break;
                }
            }
        }

        return roles;
    }

    private static IEnumerable<int> FreeColumns(int width, Dictionary<CsvRole, int> roles) =>
        Enumerable.Range(0, width).Where(c => !roles.ContainsValue(c));

    private static int? FindDateColumn(List<CsvRow> data, int width, Dictionary<CsvRole, int> roles) =>
        FreeColumns(width, roles).Cast<int?>().FirstOrDefault(c =>
        {
            var values = data.Select(r => r[c]).Where(v => v.Length > 0).ToList();
            return values.Count > 0 && values.Count(DateText.LooksLikeDate) * 10 >= values.Count * 9;
        });

    private static int? FindAmountColumn(List<CsvRow> data, int width, Dictionary<CsvRole, int> roles) =>
        FreeColumns(width, roles).Cast<int?>().FirstOrDefault(c =>
        {
            var values = data.Select(r => r[c]).Where(v => v.Length > 0).ToList();
            return values.Count > 0
                && values.Count(v => AmountText.TryParse(v, AmountText.AutoSeparator, out _)) * 10 >= values.Count * 9
                && values.Any(v => v.Contains('.') || v.Contains(','));
        });

    private static int? FindTextColumn(List<CsvRow> data, int width, Dictionary<CsvRole, int> roles) =>
        FreeColumns(width, roles)
            .Select(c => (Column: c, Values: data.Select(r => r[c]).Where(v => v.Length > 0).ToList()))
            .Where(x => x.Values.Count > 0 && x.Values.Count(v => AmountText.TryParse(v, AmountText.AutoSeparator, out _)) * 2 < x.Values.Count)
            .OrderByDescending(x => x.Values.Average(v => v.Length))
            .Select(x => (int?)x.Column)
            .FirstOrDefault();
}
