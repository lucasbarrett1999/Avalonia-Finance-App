using System.Text;

namespace Keel.Infrastructure.Import.Csv;

/// <summary>What a CSV column holds.</summary>
internal enum CsvRole
{
    Date,
    Amount,
    Debit,
    Credit,
    Type,
    Payee,
    Memo,
    Id,
    CheckNumber,
    Category,
    Status,
    Currency,
}

/// <summary>Direction implied by a transaction-type value.</summary>
internal enum TypeDirection
{
    Unknown,
    Outflow,
    Inflow,
}

/// <summary>
/// Header names and type words seen in US and European bank exports. Header names are matched
/// after <see cref="NormalizeHeader"/>; within a role, earlier names win.
/// </summary>
internal static class CsvVocabulary
{
    /// <summary>Roles in assignment order with their header names in priority order.</summary>
    public static IReadOnlyList<(CsvRole Role, string[] Names)> Headers { get; } =
    [
        (CsvRole.Date, ["transaction date", "trans date", "date", "posting date", "posted date", "post date",
            "booking date", "date posted", "effective date", "value date", "buchungstag", "datum", "fecha"]),
        (CsvRole.Amount, ["amount", "transaction amount", "amt", "net amount", "value", "betrag", "montant", "importe"]),
        (CsvRole.Debit, ["debit", "debits", "debit amount", "amount debit", "withdrawal", "withdrawals",
            "withdrawal amount", "money out", "paid out", "outflow", "charges", "soll"]),
        (CsvRole.Credit, ["credit", "credits", "credit amount", "amount credit", "deposit", "deposits",
            "deposit amount", "money in", "paid in", "inflow", "deposits/credits", "haben"]),
        (CsvRole.Type, ["type", "transaction type", "trans type", "details", "dr/cr", "cr/dr", "debit/credit",
            "credit/debit", "credit debit indicator", "transaction code"]),
        (CsvRole.Payee, ["payee", "description", "transaction description", "merchant", "merchant name", "name",
            "payee name", "narrative", "counterparty", "beneficiary", "original description", "vendor"]),
        (CsvRole.Memo, ["memo", "notes", "note", "extended description", "extended details", "additional info",
            "additional information", "remarks", "comment", "comments", "purpose", "description"]),
        (CsvRole.Id, ["transaction id", "trans id", "reference number", "reference", "reference no", "ref",
            "ref #", "fitid", "id", "confirmation number"]),
        (CsvRole.CheckNumber, ["check number", "check #", "check no", "check", "check or slip #", "cheque number",
            "cheque no", "chk #", "chk no", "num", "serial number"]),
        (CsvRole.Category, ["category", "transaction category"]),
        (CsvRole.Status, ["status", "transaction status"]),
        (CsvRole.Currency, ["currency", "ccy", "curr"]),
    ];

    private static readonly HashSet<string> AllHeaderNames =
        Headers.SelectMany(h => h.Names).Concat(["balance", "running bal", "running balance", "card no", "account number"])
            .ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> InflowWords = new(StringComparer.Ordinal)
    {
        "credit", "cr", "c", "deposit", "dep", "dslip", "refund", "return", "interest", "int", "dividend", "div",
        "directdep", "in", "received", "reversal", "inflow",
    };

    private static readonly HashSet<string> OutflowWords = new(StringComparer.Ordinal)
    {
        "debit", "dr", "d", "withdrawal", "withdrawl", "wd", "purchase", "sale", "check", "cheque", "chk", "fee",
        "atm", "pos", "charge", "paid", "out", "sent", "directdebit", "outflow",
    };

    private static readonly HashSet<string> PendingWords = new(StringComparer.Ordinal)
    {
        "pending", "pend", "authorized", "authorised", "unposted", "hold",
    };

    /// <summary>Lower-case, parentheses removed, punctuation except <c># / &amp;</c> turned into spaces, collapsed.</summary>
    public static string NormalizeHeader(string header)
    {
        var sb = new StringBuilder(header.Length);
        var depth = 0;
        foreach (var c in header.Trim().Trim('"').ToLowerInvariant())
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                sb.Append(char.IsLetterOrDigit(c) || c is '#' or '/' or '&' ? c : ' ');
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>True when the header cell is a known column name.</summary>
    public static bool IsKnownHeader(string cell) => AllHeaderNames.Contains(NormalizeHeader(cell));

    /// <summary>
    /// Classifies a type value such as <c>DEBIT</c>, <c>Withdrawal</c>, <c>ACH_CREDIT</c> or
    /// <c>CHECK_DEPOSIT</c>. Inflow words win over outflow words when both appear.
    /// </summary>
    public static TypeDirection ClassifyType(string value)
    {
        var words = NormalizeHeader(value.Replace('_', ' ')).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return TypeDirection.Unknown;
        }

        if (words.Any(InflowWords.Contains))
        {
            return TypeDirection.Inflow;
        }

        return words.Any(OutflowWords.Contains) ? TypeDirection.Outflow : TypeDirection.Unknown;
    }

    /// <summary>True for status values meaning "not yet posted".</summary>
    public static bool IsPendingStatus(string value) =>
        NormalizeHeader(value).Split(' ').Any(PendingWords.Contains);
}
