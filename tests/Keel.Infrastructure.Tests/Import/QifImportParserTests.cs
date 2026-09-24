using System.Text;
using Keel.Application.Import;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Import.Qif;

namespace Keel.Infrastructure.Tests.Import;

public class QifImportParserTests
{
    private static ParseResult Parse(string text, ImportOptions? options = null) =>
        QifImportParser.Parse(Encoding.UTF8.GetBytes(text), options ?? ImportOptions.Default);

    [Theory]
    [InlineData("1/ 2'26", 2026, 1, 2)]
    [InlineData("1/2'26", 2026, 1, 2)]
    [InlineData("12/31' 5", 2005, 12, 31)]
    [InlineData("01/02/2026", 2026, 1, 2)]
    [InlineData("1/2/98", 1998, 1, 2)]
    [InlineData("1/2/06", 2006, 1, 2)]
    [InlineData("01-02-2026", 2026, 1, 2)]
    [InlineData("1.2.2026", 2026, 1, 2)]
    [InlineData("2026-01-02", 2026, 1, 2)]
    public void Reads_quicken_date_variants(string value, int y, int m, int d)
    {
        var result = Parse($"!Type:Bank\nD{value}\nT-1.00\nPX\n^\nD1/13/2026\nT-1.00\nPY\n^\n");
        result.Transactions[0].Date.ShouldBe(new DateOnly(y, m, d));
    }

    [Theory]
    [InlineData("!Type:Bank", "Bank")]
    [InlineData("!Type:CCard", "CCard")]
    [InlineData("!Type:Cash", "Cash")]
    [InlineData("!Type:Oth L", "Oth L")]
    [InlineData("!Type:Oth A", "Oth A")]
    [InlineData("!type:bank ", "Bank")]
    public void Reads_every_non_investment_account_type(string header, string type)
    {
        var result = Parse($"{header}\nD1/13/2026\nT-5.00\nPX\n^\n");

        result.Account!.AccountType.ShouldBe(type);
        result.Transactions.ShouldHaveSingleItem().Amount.ShouldBe(-500);
    }

    [Fact]
    public void Day_first_files_are_detected()
    {
        var result = Parse("!Type:Bank\nD13/01/2026\nT-1.00\nPX\n^\nD02/01/2026\nT-1.00\nPY\n^\n");

        result.Warnings.ShouldBeEmpty();
        result.Transactions.Select(t => t.Date).ShouldBe([new DateOnly(2026, 1, 13), new DateOnly(2026, 1, 2)]);
    }

    [Fact]
    public void Ambiguous_dates_warn_and_honour_the_answer()
    {
        const string qif = "!Type:Bank\nD01/02/2026\nT-1.00\nPX\n^\nD03/04/2026\nT-1.00\nPY\n^\n";

        var result = Parse(qif);
        result.Warnings.ShouldHaveSingleItem().Code.ShouldBe(ImportWarningCode.AmbiguousDateFormat);
        result.Transactions[0].Date.ShouldBe(new DateOnly(2026, 1, 2));

        var answered = Parse(qif, ImportOptions.Default with { PreferredDateOrder = DateOrder.DayFirst });
        answered.Warnings.ShouldBeEmpty();
        answered.Transactions[0].Date.ShouldBe(new DateOnly(2026, 2, 1));
    }

    [Fact]
    public void U_amount_is_used_when_T_is_missing_and_european_amounts_parse()
    {
        var result = Parse("!Type:Bank\nD1/13/2026\nU-1,234.56\nPX\n^\nD1/14/2026\nT-12,34\nPY\n^\n");
        result.Transactions.Select(t => t.Amount).ShouldBe([-123456L, -1234L]);
    }

    [Fact]
    public void Missing_final_caret_keeps_the_record_and_warns()
    {
        var result = Parse("!Type:Bank\nD1/13/2026\nT-1.00\nPX");

        result.Transactions.ShouldHaveSingleItem();
        result.Warnings.ShouldHaveSingleItem().Code.ShouldBe(ImportWarningCode.MalformedMarkup);
    }

    [Fact]
    public void Bad_records_are_skipped_with_warnings()
    {
        var result = Parse("!Type:Bank\nDnot a date\nT-1.00\n^\nD1/13/2026\nTabc\n^\nD1/14/2026\nT-2.00\n^\n");

        result.Transactions.ShouldHaveSingleItem().Amount.ShouldBe(-200);
        result.Warnings.Select(w => (w.Code, w.Line)).ShouldBe(
            [(ImportWarningCode.InvalidDate, (int?)2), (ImportWarningCode.InvalidAmount, (int?)5)]);
    }

    [Fact]
    public void Splits_without_categories_and_unknown_fields_are_kept()
    {
        var result = Parse("!Type:Bank\nD1/13/2026\nT-10.00\nPX\n$-4.00\nEfirst\nS\n$-6.00\nFreimbursable\n^\n");
        var t = result.Transactions.ShouldHaveSingleItem();

        t.Splits.ShouldBe([new ParsedSplit(null, "first", -400), new ParsedSplit(null, null, -600)]);
        t.Extras["F"].ShouldBe("reimbursable");
    }

    [Fact]
    public void Category_and_class_lists_are_ignored_silently()
    {
        var result = Parse("!Type:Cat\nNGroceries\nE\n^\n!Type:Bank\nD1/13/2026\nT-1.00\nPX\n^\n");
        result.Warnings.ShouldBeEmpty();
        result.Transactions.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("money.qif", "", true)]
    [InlineData("download", "!Type:Bank\n", true)]
    [InlineData("download", "!Account\n", true)]
    [InlineData("download", "﻿!Option:AutoSwitch\n", true)]
    [InlineData("download.csv", "Date,Amount", false)]
    public void CanParse_by_extension_or_content(string fileName, string head, bool expected)
    {
        new QifImportParser().CanParse(fileName, Encoding.UTF8.GetBytes(head)).ShouldBe(expected);
    }
}
