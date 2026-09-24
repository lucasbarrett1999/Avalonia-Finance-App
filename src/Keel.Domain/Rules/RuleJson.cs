using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keel.Domain.Rules;

/// <summary>
/// The versioned JSON format of <c>Rule.ConditionsJson</c> and <c>Rule.ActionsJson</c>
/// (PRD 6.2). Property names are camelCase, enums are camelCase strings, nulls are omitted, and
/// each document carries <c>"version"</c>. A document without a version is read as version 1;
/// a newer version is refused so an older app never rewrites a rule it does not understand.
/// </summary>
public static class RuleJson
{
    /// <summary>The format version this code writes and the newest it reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Serializer options for the rule format.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Serializes conditions.</summary>
    public static string Serialize(RuleConditionSet conditions) => JsonSerializer.Serialize(conditions, Options);

    /// <summary>Serializes actions.</summary>
    public static string Serialize(RuleActionSet actions) => JsonSerializer.Serialize(actions, Options);

    /// <summary>Reads conditions. Throws <see cref="RuleFormatException"/> when the JSON is invalid or newer.</summary>
    public static RuleConditionSet DeserializeConditions(string? json)
    {
        var result = Read<RuleConditionSet>(json, "conditions") ?? new RuleConditionSet();
        CheckVersion(result.Version, "conditions");
        return result with { Version = CurrentVersion, Conditions = result.Conditions ?? [] };
    }

    /// <summary>Reads actions. Throws <see cref="RuleFormatException"/> when the JSON is invalid or newer.</summary>
    public static RuleActionSet DeserializeActions(string? json)
    {
        var result = Read<RuleActionSet>(json, "actions") ?? new RuleActionSet();
        CheckVersion(result.Version, "actions");
        return result with { Version = CurrentVersion, Actions = result.Actions ?? [] };
    }

    private static T? Read<T>(string? json, string what)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new RuleFormatException($"The rule's {what} are not valid: {ex.Message}", ex);
        }
    }

    private static void CheckVersion(int version, string what)
    {
        if (version > CurrentVersion)
        {
            throw new RuleFormatException(
                $"The rule's {what} use format version {version}, which is newer than this version of Keel understands ({CurrentVersion}).");
        }

        if (version < 0)
        {
            throw new RuleFormatException($"The rule's {what} have an invalid format version {version}.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>A stored rule's JSON could not be read.</summary>
public sealed class RuleFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RuleFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public RuleFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public RuleFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
