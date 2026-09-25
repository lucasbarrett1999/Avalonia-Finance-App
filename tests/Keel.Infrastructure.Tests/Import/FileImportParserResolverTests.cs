using System.Text;
using Keel.Application.Import;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Import.Csv;
using Keel.Infrastructure.Import.Ofx;
using Keel.Infrastructure.Import.Qif;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure.Tests.Import;

public class FileImportParserResolverTests
{
    [Theory]
    [InlineData("statement.ofx", "OFXHEADER:100", typeof(OfxImportParser))]
    [InlineData("export.csv", "OFXHEADER:100", typeof(OfxImportParser))] // content beats a wrong extension
    [InlineData("statement.qfx", "", typeof(OfxImportParser))]
    [InlineData("download", "<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\"?><OFX>", typeof(OfxImportParser))]
    [InlineData("money.txt", "!Type:Bank\nD1/2/2026\n", typeof(QifImportParser))]
    [InlineData("money.qif", "", typeof(QifImportParser))]
    [InlineData("export.csv", "Date,Amount\n", typeof(CsvImportParser))]
    [InlineData("download", "Date;Amount\n", typeof(CsvImportParser))]
    public void Picks_the_parser_by_content_then_extension(string fileName, string head, Type expected)
    {
        new FileImportParserResolver().Resolve(fileName, Encoding.UTF8.GetBytes(head)).ShouldBeOfType(expected);
    }

    [Fact]
    public void Returns_null_for_unknown_files()
    {
        new FileImportParserResolver().Resolve("photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0]).ShouldBeNull();
    }

    [Fact]
    public void Registers_parsers_and_resolver_in_di()
    {
        using var provider = new ServiceCollection().AddKeelFileImportParsers().BuildServiceProvider();

        provider.GetServices<IFileImportParser>().Count().ShouldBe(6, "OFX/QFX, QIF, CSV, and the YNAB register, YNAB budget and Monarch exports (M9)");
        provider.GetRequiredService<IFileImportParserResolver>()
            .Resolve("a.qif", []).ShouldBeOfType<QifImportParser>();
    }
}
