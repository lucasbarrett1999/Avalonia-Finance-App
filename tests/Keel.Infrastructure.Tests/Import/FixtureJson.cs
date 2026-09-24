using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Keel.Application.Import;

namespace Keel.Infrastructure.Tests.Import;

/// <summary>
/// Projects a <see cref="ParseResult"/> to the JSON shape stored in the fixtures'
/// <c>*.expected.json</c> files: null values, empty collections and false pending flags are
/// omitted; warning messages are left out so rewording them does not break fixtures.
/// </summary>
internal static class FixtureJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The fixtures folder next to the test binaries.</summary>
    public static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "Import", "Fixtures");

    /// <summary>The fixtures folder in the source tree (used only when regenerating).</summary>
    public static string SourceDirectory([CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "Fixtures");

    public static JsonObject Project(ParseResult result)
    {
        var root = new JsonObject
        {
            ["format"] = result.Format.ToString(),
            ["encoding"] = result.EncodingName,
        };

        if (result.CsvLayout is { } layout)
        {
            root["csvLayout"] = new JsonObject
            {
                ["mapping"] = JsonSerializer.SerializeToNode(layout.Mapping, Options),
                ["headers"] = JsonSerializer.SerializeToNode(layout.Headers, Options),
                ["dateFormatCandidates"] = JsonSerializer.SerializeToNode(layout.DateFormatCandidates, Options),
                ["isDateFormatAmbiguous"] = layout.IsDateFormatAmbiguous,
            };
        }

        if (result.Accounts.Count > 0)
        {
            root["accounts"] = JsonSerializer.SerializeToNode(result.Accounts, Options);
        }

        if (result.Warnings.Count > 0)
        {
            root["warnings"] = new JsonArray(result.Warnings.Select(w =>
            {
                var o = new JsonObject { ["code"] = w.Code.ToString() };
                if (w.Line is { } line)
                {
                    o["line"] = line;
                }

                if (w.Candidates is { Count: > 0 } candidates)
                {
                    o["candidates"] = JsonSerializer.SerializeToNode(candidates, Options);
                }

                return (JsonNode)o;
            }).ToArray());
        }

        root["transactions"] = new JsonArray(result.Transactions.Select(t =>
        {
            var o = JsonSerializer.SerializeToNode(t, Options)!.AsObject();
            if (!t.IsPending)
            {
                o.Remove("isPending");
            }

            if (t.Splits.Count == 0)
            {
                o.Remove("splits");
            }

            if (t.Extras.Count == 0)
            {
                o.Remove("extras");
            }

            return (JsonNode)o;
        }).ToArray());

        return root;
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Options).ReplaceLineEndings("\n") + "\n";
}
