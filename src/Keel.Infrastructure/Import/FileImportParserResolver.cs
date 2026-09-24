using Keel.Application.Import;
using Keel.Infrastructure.Import.Csv;
using Keel.Infrastructure.Import.Ofx;
using Keel.Infrastructure.Import.Qif;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure.Import;

/// <summary>
/// Picks a parser by content first and extension second: OFX/QFX, then QIF, then CSV (which
/// accepts any delimited text, so it goes last).
/// </summary>
public sealed class FileImportParserResolver(IEnumerable<IFileImportParser> parsers) : IFileImportParserResolver
{
    private readonly IReadOnlyList<IFileImportParser> _parsers = parsers
        .OrderBy(p => p switch { OfxImportParser => 0, QifImportParser => 1, CsvImportParser => 3, _ => 2 })
        .ToList();

    /// <summary>A resolver over the three built-in parsers.</summary>
    public FileImportParserResolver()
        : this([new OfxImportParser(), new QifImportParser(), new CsvImportParser()])
    {
    }

    /// <inheritdoc />
    public IFileImportParser? Resolve(string fileName, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        // Content wins over a misleading extension: a bank's "export.csv" that is really OFX.
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(string.Empty, head))
            {
                return parser;
            }
        }

        foreach (var parser in _parsers)
        {
            if (parser.CanParse(fileName, head))
            {
                return parser;
            }
        }

        return null;
    }
}

/// <summary>DI registration for the file import parsers.</summary>
public static class ImportParsersServiceCollectionExtensions
{
    /// <summary>Registers the OFX/QFX, QIF and CSV parsers and the resolver as singletons.</summary>
    public static IServiceCollection AddKeelFileImportParsers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFileImportParser, OfxImportParser>();
        services.AddSingleton<IFileImportParser, QifImportParser>();
        services.AddSingleton<IFileImportParser, CsvImportParser>();
        services.AddSingleton<IFileImportParserResolver>(sp => new FileImportParserResolver(sp.GetServices<IFileImportParser>()));
        return services;
    }
}
