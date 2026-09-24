using System.Text;
using Keel.Application.Import;
using Keel.Infrastructure.Import.Csv;

namespace Keel.Infrastructure.Tests.Import;

public class CsvImportParserTests
{
    private static ParseResult Parse(string text, ImportOptions? options = null, Encoding? encoding = null) =>
        CsvImportParser.Parse((encoding ?? Encoding.UTF8).GetBytes(text), options ?? ImportOptions.Default);

    private static ParseResult Parse(byte[] bytes, ImportOptions? options = null) =>
        CsvImportParser.Parse(bytes, options ?? ImportOptions.Default);

    [Fact]
    public void Ambiguous_dates_are_reported_with_both_candidates()
    {
        const string csv = "Date,Description,Amount\n01/02/2026,A,-1.00\n03/04/2026,B,-2.00\n";
        var result = Parse(csv);

        result.CsvLayout!.IsDateFormatAmbiguous.ShouldBeTrue();
        result.CsvLayout.DateFormatCandidates.ShouldBe(["MM/dd/yyyy", "dd/MM/yyyy"]);
        var warning = result.Warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe(ImportWarningCode.AmbiguousDateFormat);
        warning.Candidates.ShouldBe(["MM/dd/yyyy", "dd/MM/yyyy"]);
        result.Transactions.Select(t => t.Date).ShouldBe([new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4)]);

        var answered = Parse(csv, ImportOptions.Default with { PreferredDateOrder = DateOrder.DayFirst });
        answered.Warnings.ShouldBeEmpty();
        answered.CsvLayout!.IsDateFormatAmbiguous.ShouldBeFalse();
        answered.Transactions.Select(t => t.Date).ShouldBe([new DateOnly(2026, 2, 1), new DateOnly(2026, 4, 3)]);
    }

    [Fact]
    public void Explicit_mapping_overrides_detection_and_is_deterministic()
    {
        const string csv = "Date,Description,Amount\n01/02/2026,Coffee,4.50\n01/03/2026,Refund,-2.00\n";
        var mapping = new CsvColumnMapping
        {
            DateColumn = 0,
            DateFormat = "dd/MM/yyyy",
            PayeeColumn = 1,
            AmountColumn = 2,
            SignConvention = CsvSignConvention.OutflowPositive,
        };
        var options = ImportOptions.Default with { CsvMapping = mapping };

        var first = Parse(csv, options);
        var second = Parse(csv, options);

        first.CsvLayout!.Mapping.ShouldBe(mapping);
        first.Transactions.Select(t => (t.Date, t.Amount)).ShouldBe(
            [(new DateOnly(2026, 2, 1), -450L), (new DateOnly(2026, 3, 1), 200L)]);
        second.Transactions.Select(t => (t.Date, t.Amount, t.PayeeRaw)).ShouldBe(first.Transactions.Select(t => (t.Date, t.Amount, t.PayeeRaw)));
    }

    [Fact]
    public void Amount_with_withdrawal_and_deposit_words()
    {
        const string csv = "Date,Description,Amount,Transaction Type\n2026-01-02,Rent,1500.00,Withdrawal\n2026-01-03,Pay,2000.00,Deposit\n2026-01-04,Check,12.00,CHECK_PAID\n2026-01-05,Mobile deposit,40.00,CHECK_DEPOSIT\n";
        var result = Parse(csv);

        result.CsvLayout!.Mapping.AmountLayout.ShouldBe(CsvAmountLayout.AmountWithType);
        result.Transactions.Select(t => t.Amount).ShouldBe([-150000L, 200000L, -1200L, 4000L]);
    }

    [Fact]
    public void Unknown_type_keeps_the_amount_and_warns()
    {
        var mapping = new CsvColumnMapping
        {
            DateColumn = 0,
            DateFormat = "yyyy-MM-dd",
            PayeeColumn = 1,
            AmountColumn = 2,
            TypeColumn = 3,
            AmountLayout = CsvAmountLayout.AmountWithType,
        };
        var result = Parse("2026-01-02,Adjustment,5.00,ADJ\n", ImportOptions.Default with { CsvMapping = mapping with { HasHeader = false } });

        result.Transactions.ShouldHaveSingleItem().Amount.ShouldBe(500);
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.UnknownTransactionType && w.Line == 1);
    }

    [Fact]
    public void Debit_and_credit_columns_accept_negative_debits()
    {
        const string csv = "Date,Description,Debit,Credit\n2026-01-02,Store,-12.50,\n2026-01-03,Refund,,3.00\n";
        Parse(csv).Transactions.Select(t => t.Amount).ShouldBe([-1250L, 300L]);
    }

    [Theory]
    [InlineData('\t')]
    [InlineData('|')]
    [InlineData(';')]
    public void Sniffs_other_delimiters(char delimiter)
    {
        var d = delimiter;
        var csv = $"Date{d}Payee{d}Amount\n2026-01-02{d}Joe Inc{d}-1.00\n2026-01-03{d}Ann{d}2.00\n";
        var result = Parse(csv);

        result.CsvLayout!.Mapping.Delimiter.ShouldBe(delimiter);
        result.Transactions.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("utf-16", true)]
    [InlineData("utf-16", false)]
    [InlineData("utf-16BE", true)]
    [InlineData("utf-16BE", false)]
    public void Reads_utf16_with_and_without_bom(string encodingName, bool bom)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var body = encoding.GetBytes("Date,Description,Amount\n2026-01-02,Café Crème,-4.50\n");
        var bytes = bom ? [.. encoding.Preamble, .. body] : body;

        var result = Parse(bytes);

        result.EncodingName.ShouldBe(encoding.WebName);
        result.Transactions.ShouldHaveSingleItem().PayeeRaw.ShouldBe("Café Crème");
    }

    [Fact]
    public void Windows1252_is_detected_and_can_be_forced()
    {
        var bytes = Keel.Infrastructure.Import.TextDecoder.Windows1252.GetBytes("Date,Description,Amount\n2026-01-02,Café,-4.50\n");

        Parse(bytes).Transactions.ShouldHaveSingleItem().PayeeRaw.ShouldBe("Café");
        var forced = Parse(bytes, ImportOptions.Default with { EncodingName = "utf-8" });
        forced.Warnings.ShouldContain(w => w.Code == ImportWarningCode.EncodingFallback);
        forced.Transactions.ShouldHaveSingleItem().PayeeRaw.ShouldBe("Café");
        Parse(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-01-02,Café,-4.50\n"), ImportOptions.Default with { EncodingName = "windows-1252" })
            .Transactions.ShouldHaveSingleItem().PayeeRaw.ShouldBe("CafÃ©");
    }

    [Fact]
    public void Pending_status_currency_id_and_check_columns()
    {
        const string csv = "Date,Description,Amount,Status,Currency,Transaction ID,Check Number\n" +
            "2026-01-02,Hold,-1.00,Pending,CAD,T1,\n" +
            "2026-01-03,Posted,-2.00,Posted,??,T1,1001\n";
        var result = Parse(csv);

        var (pending, posted) = (result.Transactions[0], result.Transactions[1]);
        pending.IsPending.ShouldBeTrue();
        pending.Currency.ShouldBe("CAD");
        pending.ProviderTransactionId.ShouldBe("T1");
        posted.IsPending.ShouldBeFalse();
        posted.Currency.ShouldBe("USD");
        posted.CheckNumber.ShouldBe("1001");
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.DuplicateProviderId && w.Line == 3);
    }

    [Fact]
    public void Line_numbers_follow_multi_line_quoted_fields_and_blank_lines()
    {
        const string csv = "Date,Description,Amount\n\n2026-01-02,\"Two\nlines\",-1.00\n2026-01-03,Next,-2.00\n";
        var result = Parse(csv);

        result.Transactions.Select(t => t.SourceLine).ShouldBe([3, 5]);
        result.Transactions[0].PayeeRaw.ShouldBe("Two\nlines");
    }

    [Fact]
    public void Footer_and_bad_rows_become_warnings()
    {
        const string csv = "Date,Description,Amount\n2026-01-02,A,-1.00\n2026-01-03,B,oops\nTotal,,-1.00\n";
        var result = Parse(csv);

        result.Transactions.ShouldHaveSingleItem();
        result.Warnings.Select(w => (w.Code, w.Line)).ShouldBe(
            [(ImportWarningCode.InvalidAmount, (int?)3), (ImportWarningCode.InvalidDate, (int?)4)]);
    }

    [Fact]
    public void All_positive_amounts_ask_for_the_sign_convention()
    {
        var result = Parse("Date,Description,Amount\n2026-01-02,A,1.00\n2026-01-03,B,2.00\n");
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.SignConventionGuessed);
        result.CsvLayout!.Mapping.SignConvention.ShouldBe(CsvSignConvention.InflowPositive);
    }

    [Theory]
    [InlineData("Description,Amount\nA,-1.00\n")]
    [InlineData("Date,Description\n2026-01-02,A\n")]
    [InlineData("Date,Description,Amount\n01/13/2026,A,-1.00\n13/01/2026,B,-1.00\n")]
    public void Undetectable_layouts_return_a_mapping_warning(string csv)
    {
        var result = Parse(csv);
        result.CsvLayout.ShouldBeNull();
        result.Transactions.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.Code == ImportWarningCode.InvalidMapping);
    }

    [Fact]
    public void Mapping_columns_past_the_row_end_skip_rows_instead_of_throwing()
    {
        var mapping = new CsvColumnMapping { DateColumn = 9, DateFormat = "yyyy-MM-dd", AmountColumn = 1, HasHeader = false };
        var result = Parse("2026-01-02,1.00\n", ImportOptions.Default with { CsvMapping = mapping });

        result.Transactions.ShouldBeEmpty();
        result.Warnings.Select(w => w.Code).ShouldBe([ImportWarningCode.InvalidDate, ImportWarningCode.NoTransactions]);
    }

    [Fact]
    public void Empty_file_yields_a_warning()
    {
        Parse(string.Empty).Warnings.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("export.CSV", "", true)]
    [InlineData("export.tsv", "", true)]
    [InlineData("download", "Date,Amount\n", true)]
    [InlineData("download", "<OFX>", false)]
    [InlineData("download", "!Type:Bank", false)]
    [InlineData("download", "OFXHEADER:100", false)]
    [InlineData("download", "just words", false)]
    public void CanParse_by_extension_or_content(string fileName, string head, bool expected)
    {
        new CsvImportParser().CanParse(fileName, Encoding.UTF8.GetBytes(head)).ShouldBe(expected);
    }
}
