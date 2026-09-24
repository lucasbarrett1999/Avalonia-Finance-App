using System.Text;
using Keel.Application.Import;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Import.Ofx;

namespace Keel.Infrastructure.Tests.Import;

public class OfxImportParserTests
{
    private const string SgmlHeader = "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nSECURITY:NONE\nENCODING:USASCII\nCHARSET:1252\n\n";

    private static ParseResult Parse(string text, Encoding? encoding = null) =>
        OfxImportParser.Parse((encoding ?? Encoding.UTF8).GetBytes(text), ImportOptions.Default);

    private static string Trn(string fitid, string amount, string name = "PAYEE", string date = "20260105") =>
        $"<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>{date}<TRNAMT>{amount}<FITID>{fitid}<NAME>{name}</STMTTRN>\n";

    [Theory]
    [InlineData("20260105", 2026, 1, 5)]
    [InlineData("20260105120000", 2026, 1, 5)]
    [InlineData("20260105120000.000", 2026, 1, 5)]
    [InlineData("20260105120000.000[-5:EST]", 2026, 1, 5)]
    [InlineData("20260105235959[+9.5:ACST]", 2026, 1, 5)]
    [InlineData("20260105000000[0:GMT]", 2026, 1, 5)]
    [InlineData(" 20261231 ", 2026, 12, 31)]
    public void Reads_ofx_dates_as_the_stated_civil_date(string value, int y, int m, int d)
    {
        OfxImportParser.OfxDate(value).ShouldBe(new DateOnly(y, m, d));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026010")]
    [InlineData("2026-01-05")]
    [InlineData("20261305")]
    [InlineData("20260230")]
    public void Rejects_invalid_ofx_dates(string? value)
    {
        OfxImportParser.OfxDate(value).ShouldBeNull();
    }

    [Theory]
    [InlineData("AT&amp;T", "AT&T")]
    [InlineData("AT&T", "AT&T")]
    [InlineData("&lt;b&gt; &quot;x&quot; &apos;y&apos;", "<b> \"x\" 'y'")]
    [InlineData("Caf&#233; &#xE9;", "Café é")]
    [InlineData("A &unknown; B", "A &unknown; B")]
    public void Decodes_entities(string raw, string expected)
    {
        OfxReader.DecodeEntities(raw).ShouldBe(expected);
    }

    [Fact]
    public void Tolerates_missing_aggregate_end_tags()
    {
        var text = SgmlHeader + "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD<BANKACCTFROM><ACCTID>1</BANKACCTFROM>\n<BANKTRANLIST>\n"
            + "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260105<TRNAMT>-1.00<FITID>A<NAME>ONE\n"
            + "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260106<TRNAMT>-2.00<FITID>B<NAME>TWO\n"
            + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var result = Parse(text);

        result.Transactions.Select(t => t.ProviderTransactionId).ShouldBe(["A", "B"]);
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.MalformedMarkup);
    }

    [Fact]
    public void Truncated_files_keep_what_was_read_and_warn()
    {
        var text = SgmlHeader + "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>\n" + Trn("A", "-1.00") + "<STMTTRN><TRNTYPE>DEB";
        var result = Parse(text);

        result.Transactions.ShouldHaveSingleItem().ProviderTransactionId.ShouldBe("A");
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.MalformedMarkup);
    }

    [Fact]
    public void Reads_several_statements_with_their_accounts()
    {
        var text = SgmlHeader + "<OFX>\n<BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD<BANKACCTFROM><BANKID>9<ACCTID>CHK<ACCTTYPE>CHECKING</BANKACCTFROM><BANKTRANLIST>\n"
            + Trn("A", "-500.00", "PAYMENT TO CARD")
            + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1>\n"
            + "<CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CURDEF>CAD<CCACCTFROM><ACCTID>CARD</CCACCTFROM><BANKTRANLIST>\n"
            + Trn("B", "500.00", "PAYMENT RECEIVED")
            + "</BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>";
        var result = Parse(text);

        result.Accounts.Select(a => (a.AccountId, a.AccountType, a.Currency)).ShouldBe(
            [("CHK", "CHECKING", "USD"), ("CARD", "CREDITCARD", "CAD")]);
        result.Transactions.Select(t => (t.SourceAccountId, t.Currency, t.Amount)).ShouldBe(
            [("CHK", "USD", -50000L), ("CARD", "CAD", 50000L)]);
        result.Transactions.Select(t => t.SourceIndex).ShouldBe([0, 1]);
    }

    [Fact]
    public void Accepts_ccstmttrn_elements()
    {
        var text = SgmlHeader + "<OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CCACCTFROM><ACCTID>C</CCACCTFROM><BANKTRANLIST>\n"
            + "<CCSTMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260105<TRNAMT>-3.00<FITID>Z<NAME>SHOP</CCSTMTTRN>\n"
            + "</BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>";
        Parse(text).Transactions.ShouldHaveSingleItem().Amount.ShouldBe(-300);
    }

    [Fact]
    public void Duplicate_fitids_warn_and_missing_fields_skip_with_warnings()
    {
        var text = SgmlHeader + "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>\n"
            + Trn("A", "-1.00") + Trn("A", "-2.00")
            + "<STMTTRN><TRNTYPE>DEBIT<TRNAMT>-3.00<FITID>C<NAME>NO DATE</STMTTRN>\n"
            + "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260105<TRNAMT>N/A<FITID>D<NAME>BAD AMOUNT</STMTTRN>\n"
            + "<STMTTRN><TRNTYPE>DEBIT<DTUSER>20260107<TRNAMT>-4.00<FITID>E<PAYEE><NAME>PAYEE AGG<ADDR1>1 MAIN</PAYEE></STMTTRN>\n"
            + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var result = Parse(text);

        result.Transactions.Select(t => t.ProviderTransactionId).ShouldBe(["A", "A", "E"]);
        result.Transactions[2].PayeeRaw.ShouldBe("PAYEE AGG");
        result.Transactions[2].Date.ShouldBe(new DateOnly(2026, 1, 7));
        result.Warnings.Select(w => w.Code).ShouldBe(
            [ImportWarningCode.DuplicateProviderId, ImportWarningCode.InvalidDate, ImportWarningCode.InvalidAmount]);
    }

    [Fact]
    public void Memo_becomes_the_payee_when_name_is_missing()
    {
        var text = SgmlHeader + "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>\n"
            + "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260105<TRNAMT>-1.00<FITID>A<MEMO>ONLY MEMO</STMTTRN>\n"
            + "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260105<TRNAMT>-1.00<FITID>B<NAME>SAME<MEMO>SAME</STMTTRN>\n"
            + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var result = Parse(text);

        result.Transactions[0].PayeeRaw.ShouldBe("ONLY MEMO");
        result.Transactions[0].Memo.ShouldBeNull();
        result.Transactions[1].Memo.ShouldBeNull();
    }

    [Fact]
    public void Lower_case_tags_lf_and_european_amounts_are_accepted()
    {
        var text = "<ofx><bankmsgsrsv1><stmttrnrs><stmtrs><banktranlist>\n<stmttrn><trntype>DEBIT<dtposted>20260105<trnamt>-12,34<fitid>A<name>X</stmttrn>\n</banktranlist></stmtrs></stmttrnrs></bankmsgsrsv1></ofx>";
        Parse(text).Transactions.ShouldHaveSingleItem().Amount.ShouldBe(-1234);
    }

    [Fact]
    public void Declared_utf8_with_1252_bytes_falls_back_and_warns()
    {
        var text = "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nENCODING:UTF-8\nCHARSET:NONE\n\n<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>\n"
            + Trn("A", "-1.00", "CAFÉ") + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var result = Parse(text, TextDecoder.Windows1252);

        result.Transactions.ShouldHaveSingleItem().PayeeRaw.ShouldBe("CAFÉ");
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.EncodingFallback);
    }

    [Fact]
    public void Xml_declared_latin1_is_honoured()
    {
        var text = "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n<?OFX OFXHEADER=\"200\" VERSION=\"211\"?>\n<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>"
            + "<STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260105</DTPOSTED><TRNAMT>-1.00</TRNAMT><FITID>A</FITID><NAME>Peña</NAME></STMTTRN>"
            + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var result = Parse(text, Encoding.Latin1);

        result.EncodingName.ShouldBe("iso-8859-1");
        result.Transactions.ShouldHaveSingleItem().PayeeRaw.ShouldBe("Peña");
    }

    [Fact]
    public void Files_without_ofx_or_transactions_warn()
    {
        Parse("hello").Warnings.Select(w => w.Code).ShouldBe([ImportWarningCode.MalformedMarkup, ImportWarningCode.NoTransactions]);
        Parse(SgmlHeader + "<OFX></OFX>").Warnings.Select(w => w.Code).ShouldBe([ImportWarningCode.NoTransactions]);
    }

    [Theory]
    [InlineData("statement.ofx", "", true)]
    [InlineData("statement.QFX", "", true)]
    [InlineData("download", "OFXHEADER:100", true)]
    [InlineData("download", "<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\"?><OFX>", true)]
    [InlineData("download.csv", "Date,Amount", false)]
    public void CanParse_by_extension_or_content(string fileName, string head, bool expected)
    {
        new OfxImportParser().CanParse(fileName, Encoding.UTF8.GetBytes(head)).ShouldBe(expected);
    }
}
