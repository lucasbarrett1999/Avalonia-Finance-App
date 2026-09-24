namespace Keel.Application.Import;

/// <summary>
/// Turns a file's <see cref="ParseResult"/> into an <see cref="ImportBatch"/> for the chosen
/// account (F-TXN-2: "import is into a chosen account"). Pure.
/// </summary>
public static class ImportBatchBuilder
{
    /// <summary>
    /// Builds the batch: for a file with several accounts (OFX statements, QIF account blocks) only
    /// the rows of <paramref name="sourceAccountId"/> (default: the first account) are taken; rows in
    /// another currency than the account's are skipped with a warning; an OFX <c>LEDGERBAL</c> of
    /// the chosen statement becomes <see cref="ImportBatch.ReportedBalance"/>.
    /// </summary>
    /// <param name="accountId">Target account.</param>
    /// <param name="accountCurrency">ISO currency of the target account.</param>
    /// <param name="result">The parse.</param>
    /// <param name="sourceAccountId">The file account to import (<see cref="DetectedAccount.AccountId"/>), or null for the first.</param>
    public static ImportBatch FromParse(Guid accountId, string accountCurrency, ParseResult result, string? sourceAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountCurrency);
        var warnings = result.Warnings.ToList();
        var statement = ChooseStatement(result, sourceAccountId);
        IEnumerable<ParsedTransaction> rows = result.Transactions;
        if (result.Accounts.Count > 1 && statement is not null)
        {
            var key = statement.AccountId;
            rows = rows.Where(r => string.Equals(r.SourceAccountId, key, StringComparison.Ordinal));
            warnings.Add(new(ImportWarningCode.OtherAccountsInFile, $"The file describes {result.Accounts.Count} accounts; one was imported."));
        }

        var incoming = new List<IncomingTransaction>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Currency, accountCurrency, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new(ImportWarningCode.CurrencyMismatch, "Row skipped: its currency differs from the account's.", row.SourceLine));
                continue;
            }

            incoming.Add(ToIncoming(row));
        }

        StatementBalance? balance = statement is { LedgerBalance: { } amount } s
            && (s.LedgerBalanceDate ?? s.StatementEnd) is { } date
            && (s.Currency is null || string.Equals(s.Currency, accountCurrency, StringComparison.OrdinalIgnoreCase))
            ? new StatementBalance(date, amount)
            : null;

        return new ImportBatch(accountId, incoming)
        {
            ReportedBalance = balance,
            Warnings = warnings,
        };
    }

    /// <summary>The file account a batch is built from: <paramref name="sourceAccountId"/>, else the first.</summary>
    public static DetectedAccount? ChooseStatement(ParseResult result, string? sourceAccountId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Accounts.FirstOrDefault(a => sourceAccountId is not null && string.Equals(a.AccountId, sourceAccountId, StringComparison.Ordinal))
            ?? result.Account;
    }

    /// <summary>Maps a parsed row onto the pipeline's incoming row.</summary>
    public static IncomingTransaction ToIncoming(ParsedTransaction row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new IncomingTransaction(
            row.Date,
            row.Amount,
            row.PayeeRaw,
            row.Memo,
            row.ProviderTransactionId,
            row.PendingTransactionId,
            row.IsPending)
        {
            CheckNumber = row.CheckNumber,
            CategoryHint = row.Category,
        };
    }
}

/// <summary>The date format names a <see cref="CsvColumnMapping.DateFormat"/> may use from the built-in list.</summary>
public static class CsvDateFormats
{
    /// <summary>Every built-in format name, in the parser's order of preference.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "yyyy-MM-dd",
        "yyyyMMdd",
        "MM/dd/yyyy",
        "dd/MM/yyyy",
        "MM/dd/yy",
        "dd/MM/yy",
        "MMM d, yyyy",
        "d MMM yyyy",
    ];
}
