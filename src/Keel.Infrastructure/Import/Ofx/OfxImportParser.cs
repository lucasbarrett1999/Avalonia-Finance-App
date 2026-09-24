using System.Globalization;
using System.Text;
using Keel.Application.Import;

namespace Keel.Infrastructure.Import.Ofx;

/// <summary>
/// OFX and QFX import (F-TXN-2): OFX 1.x SGML and OFX 2.x XML, bank (<c>STMTRS</c>) and
/// credit-card (<c>CCSTMTRS</c>) statements, several statements per file. Each <c>STMTTRN</c>
/// (or <c>CCSTMTTRN</c>) becomes a <see cref="ParsedTransaction"/> with its <c>FITID</c> as the
/// provider id. Dates are the civil date written in <c>DTPOSTED</c>; a time and a time-zone
/// suffix such as <c>[-5:EST]</c> are accepted and not converted (ADR 0006).
/// </summary>
public sealed class OfxImportParser : IFileImportParser
{
    private static readonly HashSet<string> MappedLeaves = new(StringComparer.Ordinal)
    {
        "TRNTYPE", "DTPOSTED", "TRNAMT", "FITID", "NAME", "MEMO", "CHECKNUM",
    };

    /// <inheritdoc />
    public bool CanParse(string fileName, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (fileName.EndsWith(".ofx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".qfx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = TextDecoder.DecodeHead(head);
        return text.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<OFX>", StringComparison.OrdinalIgnoreCase);
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
        var decoded = Decode(bytes, options, warnings);
        var document = OfxReader.Read(decoded.Text);
        foreach (var problem in document.Problems)
        {
            warnings.Add(new(ImportWarningCode.MalformedMarkup, problem));
        }

        var format = decoded.Text.Contains("<INTU.BID>", StringComparison.OrdinalIgnoreCase)
            ? ImportFileFormat.Qfx
            : ImportFileFormat.Ofx;

        var accounts = new List<DetectedAccount>();
        var transactions = new List<ParsedTransaction>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var statements = document.Root.Descendants("STMTRS", "CCSTMTRS").ToList();
        if (statements.Count == 0 && document.Root.Descendants("STMTTRN", "CCSTMTTRN").Any())
        {
            statements.Add(document.Root); // transactions without a statement wrapper
        }

        foreach (var statement in statements)
        {
            var account = ReadAccount(statement, options);
            accounts.Add(account);
            var currency = account.Currency ?? options.Currency;
            foreach (var trn in statement.Descendants("STMTTRN", "CCSTMTTRN"))
            {
                if (ReadTransaction(trn, currency, account.AccountId, warnings) is not { } parsed)
                {
                    continue;
                }

                if (parsed.ProviderTransactionId is { } fitid && !seenIds.Add(fitid))
                {
                    warnings.Add(new(ImportWarningCode.DuplicateProviderId, "The FITID appears more than once.", trn.Line));
                }

                transactions.Add(parsed with { SourceIndex = transactions.Count });
            }
        }

        if (transactions.Count == 0)
        {
            warnings.Add(new(ImportWarningCode.NoTransactions, "The file contains no transactions."));
        }

        return new ParseResult(format, transactions, accounts, warnings, null, decoded.Encoding.WebName);
    }

    /// <summary>
    /// Decodes with, in order: a forced encoding, a byte-order mark, strict UTF-8, then the
    /// single-byte code page the header declares (<c>CHARSET:1252</c>, <c>encoding="ISO-8859-1"</c>).
    /// </summary>
    private static DecodedText Decode(ReadOnlySpan<byte> bytes, ImportOptions options, List<ImportWarning> warnings)
    {
        var ascii = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 2048)]);
        Encoding? declared = null;
        var charset = HeaderValue(ascii, "CHARSET:") ?? XmlEncoding(ascii);
        if (charset is not null && TextDecoder.Resolve(charset) is { } resolved && resolved.IsSingleByte)
        {
            declared = resolved;
        }

        var decoded = TextDecoder.Decode(bytes, options.EncodingName, declared);
        var declaresUtf8 = string.Equals(HeaderValue(ascii, "ENCODING:"), "UTF-8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(XmlEncoding(ascii), "UTF-8", StringComparison.OrdinalIgnoreCase);
        if (decoded.UsedFallback || (declaresUtf8 && decoded.Encoding.IsSingleByte))
        {
            warnings.Add(new(ImportWarningCode.EncodingFallback, "The file declares UTF-8 but is not valid UTF-8; decoded as a single-byte code page."));
        }

        return decoded;
    }

    private static string? HeaderValue(string head, string key)
    {
        var at = head.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var end = head.IndexOfAny(['\r', '\n', '<'], at);
        return head[(at + key.Length)..(end < 0 ? head.Length : end)].Trim();
    }

    private static string? XmlEncoding(string head)
    {
        var at = head.IndexOf("encoding=", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var rest = head[(at + "encoding=".Length)..].TrimStart();
        if (rest.Length == 0 || rest[0] is not ('"' or '\''))
        {
            return null;
        }

        var end = rest.IndexOf(rest[0], 1);
        return end > 1 ? rest[1..end] : null;
    }

    private static DetectedAccount ReadAccount(OfxElement statement, ImportOptions options)
    {
        var bank = statement.Child("BANKACCTFROM");
        var card = statement.Child("CCACCTFROM");
        var from = bank ?? card;
        var list = statement.Child("BANKTRANLIST");
        var ledger = statement.Child("LEDGERBAL");
        var available = statement.Child("AVAILBAL");
        var currency = statement.Leaf("CURDEF")?.Trim().ToUpperInvariant();
        var currencyCode = Keel.Domain.Currency.IsValidCode(currency) ? currency! : Keel.Domain.Currency.Normalize(options.Currency);

        return new DetectedAccount
        {
            AccountId = from?.Leaf("ACCTID"),
            BankId = bank?.Leaf("BANKID"),
            AccountType = bank?.Leaf("ACCTTYPE") ?? (card is not null ? "CREDITCARD" : null),
            Currency = Keel.Domain.Currency.IsValidCode(currency) ? currency : null,
            LedgerBalance = Minor(ledger?.Leaf("BALAMT"), currencyCode),
            LedgerBalanceDate = OfxDate(ledger?.Leaf("DTASOF")),
            AvailableBalance = Minor(available?.Leaf("BALAMT"), currencyCode),
            StatementStart = OfxDate(list?.Leaf("DTSTART")),
            StatementEnd = OfxDate(list?.Leaf("DTEND")),
        };
    }

    private static ParsedTransaction? ReadTransaction(OfxElement trn, string currency, string? accountId, List<ImportWarning> warnings)
    {
        var date = OfxDate(trn.Leaf("DTPOSTED")) ?? OfxDate(trn.Leaf("DTUSER"));
        if (date is null)
        {
            warnings.Add(new(ImportWarningCode.InvalidDate, "Transaction skipped: DTPOSTED is missing or invalid.", trn.Line));
            return null;
        }

        var trnCurrency = trn.Child("CURRENCY")?.Leaf("CURSYM")?.Trim().ToUpperInvariant();
        if (Keel.Domain.Currency.IsValidCode(trnCurrency))
        {
            currency = trnCurrency!;
        }

        if (Minor(trn.Leaf("TRNAMT"), currency) is not { } amount)
        {
            warnings.Add(new(ImportWarningCode.InvalidAmount, "Transaction skipped: TRNAMT is missing or invalid.", trn.Line));
            return null;
        }

        var name = NullIfBlank(trn.Leaf("NAME")) ?? NullIfBlank(trn.Child("PAYEE")?.Leaf("NAME"));
        var memo = NullIfBlank(trn.Leaf("MEMO"));
        var payee = name ?? memo ?? string.Empty;
        if (name is null || string.Equals(memo, payee, StringComparison.Ordinal))
        {
            memo = null;
        }

        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in trn.Children)
        {
            if (child.Value is { Length: > 0 } value && !MappedLeaves.Contains(child.Name))
            {
                extras.TryAdd(child.Name, value);
            }
        }

        return new ParsedTransaction
        {
            Date = date.Value,
            Amount = amount,
            PayeeRaw = payee,
            Memo = memo,
            ProviderTransactionId = NullIfBlank(trn.Leaf("FITID")),
            CheckNumber = NullIfBlank(trn.Leaf("CHECKNUM")),
            Currency = currency,
            TransactionType = NullIfBlank(trn.Leaf("TRNTYPE"))?.ToUpperInvariant(),
            SourceAccountId = accountId,
            Extras = extras,
            SourceLine = trn.Line,
        };
    }

    /// <summary>
    /// Reads an OFX date-time (<c>YYYYMMDD[HHMMSS[.XXX]][[offset[:TZ]]]</c>) as the civil date
    /// its first eight digits state.
    /// </summary>
    internal static DateOnly? OfxDate(string? value)
    {
        var v = value?.Trim();
        if (v is null || v.Length < 8 || !v[..8].All(char.IsAsciiDigit))
        {
            return null;
        }

        return DateOnly.TryParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static long? Minor(string? value, string currency) =>
        AmountText.TryParseMinor(value, AmountText.AutoSeparator, currency, out var minor) ? minor : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
