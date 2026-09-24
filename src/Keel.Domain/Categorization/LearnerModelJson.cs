using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keel.Domain.Categorization;

/// <summary>
/// JSON form of a <see cref="LearnerModel"/>, for caching in the <c>Setting</c> table. Only
/// integer counts, names and options are stored; keys are written in ordinal order, so the same
/// model always produces the same text. Derived totals are recomputed on read.
/// </summary>
public static class LearnerModelJson
{
    /// <summary>Value of the <c>format</c> property.</summary>
    public const string FormatName = "keel.categoryLearner";

    /// <summary>The format version this code writes and the newest it reads.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes a model.</summary>
    public static string Serialize(LearnerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dto = new ModelDto
        {
            Format = FormatName,
            Version = CurrentVersion,
            Options = model.Options,
            Examples = model.ExampleCount,
            Categories = model.Catalog.Values
                .OrderBy(c => c.Id)
                .Select(c => new CategoryDto { Id = c.Id, Name = c.Name, Restricted = c.IsRestricted })
                .ToList(),
            Classes = Sorted(model.ClassCounts, Key),
            Payees = Sorted(model.Payees, k => k),
            Tokens = Sorted(model.Tokens, k => k),
            AmountBuckets = Sorted(model.Amounts, k => k.ToString(CultureInfo.InvariantCulture)),
            Accounts = Sorted(model.Accounts, Key),
            Weekdays = Sorted(model.Weekdays, k => ((DayOfWeek)k).ToString().ToLowerInvariant()),
            Directions = Sorted(model.Directions, k => k switch { > 0 => "inflow", < 0 => "outflow", _ => "zero" }),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Reads a model. Throws <see cref="LearnerModelFormatException"/> for invalid, foreign or newer JSON.</summary>
    public static LearnerModel Deserialize(string json)
    {
        ModelDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ModelDto>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new LearnerModelFormatException("The learner model is not valid JSON: " + ex.Message, ex);
        }

        if (dto is null || !string.Equals(dto.Format, FormatName, StringComparison.Ordinal))
        {
            throw new LearnerModelFormatException("The text is not a Keel learner model.");
        }

        if (dto.Version > CurrentVersion || dto.Version < 1)
        {
            throw new LearnerModelFormatException($"The learner model uses format version {dto.Version}; this version of Keel reads version {CurrentVersion}. Retrain the model.");
        }

        try
        {
            var options = dto.Options ?? LearnerOptions.Default;
            options.Validate();
            var classCounts = (dto.Classes ?? new Dictionary<string, int>()).ToImmutableDictionary(kv => ParseGuid(kv.Key), kv => Positive(kv.Value));
            var tokens = Table(dto.Tokens, k => k, StringComparer.Ordinal);
            var tokenTotals = new Dictionary<Guid, int>();
            foreach (var row in tokens.Rows.Values)
            {
                foreach (var (category, count) in row)
                {
                    tokenTotals[category] = tokenTotals.GetValueOrDefault(category) + count;
                }
            }

            var model = new LearnerModel(
                options,
                classCounts,
                Table(dto.Payees, k => k, StringComparer.Ordinal),
                tokens,
                tokenTotals.ToImmutableDictionary(),
                Table(dto.AmountBuckets, k => int.Parse(k, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                Table(dto.Accounts, ParseGuid),
                Table(dto.Weekdays, k => (int)Enum.Parse<DayOfWeek>(k, ignoreCase: true)),
                Table(dto.Directions, k => k switch
                {
                    "inflow" => 1,
                    "outflow" => -1,
                    "zero" => 0,
                    _ => throw new FormatException("unknown direction " + k),
                }),
                CategoryLearner.BuildCatalog((dto.Categories ?? []).Select(c => new LearnerCategory(c.Id, c.Name ?? string.Empty, c.Restricted))));
            if (model.ExampleCount != dto.Examples)
            {
                throw new FormatException("the example count does not match the category counts");
            }

            return model;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new LearnerModelFormatException("The learner model is inconsistent: " + ex.Message, ex);
        }
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static Guid ParseGuid(string text) => Guid.ParseExact(text, "D");

    private static int Positive(int value) => value > 0 ? value : throw new FormatException("counts must be positive");

    private static SortedDictionary<string, int> Sorted(ImmutableDictionary<Guid, int> row, Func<Guid, string> key)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in row)
        {
            result[key(k)] = v;
        }

        return result;
    }

    private static SortedDictionary<string, IDictionary<string, int>> Sorted<TKey>(CountTable<TKey> table, Func<TKey, string> key)
        where TKey : notnull
    {
        var result = new SortedDictionary<string, IDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (k, row) in table.Rows)
        {
            result[key(k)] = Sorted(row, Key);
        }

        return result;
    }

    private static CountTable<TKey> Table<TKey>(IDictionary<string, IDictionary<string, int>>? rows, Func<string, TKey> key, IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, ImmutableDictionary<Guid, int>>(comparer);
        foreach (var (k, row) in rows ?? new Dictionary<string, IDictionary<string, int>>())
        {
            builder.Add(key(k), row.ToImmutableDictionary(kv => ParseGuid(kv.Key), kv => Positive(kv.Value)));
        }

        return new CountTable<TKey>(builder.ToImmutable());
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class ModelDto
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public LearnerOptions? Options { get; set; }

        public int Examples { get; set; }

        public List<CategoryDto> Categories { get; set; } = [];

        public IDictionary<string, int> Classes { get; set; } = new Dictionary<string, int>();

        public IDictionary<string, IDictionary<string, int>> Payees { get; set; } = new Dictionary<string, IDictionary<string, int>>();

        public IDictionary<string, IDictionary<string, int>> Tokens { get; set; } = new Dictionary<string, IDictionary<string, int>>();

        public IDictionary<string, IDictionary<string, int>> AmountBuckets { get; set; } = new Dictionary<string, IDictionary<string, int>>();

        public IDictionary<string, IDictionary<string, int>> Accounts { get; set; } = new Dictionary<string, IDictionary<string, int>>();

        public IDictionary<string, IDictionary<string, int>> Weekdays { get; set; } = new Dictionary<string, IDictionary<string, int>>();

        public IDictionary<string, IDictionary<string, int>> Directions { get; set; } = new Dictionary<string, IDictionary<string, int>>();
    }

    private sealed class CategoryDto
    {
        public Guid Id { get; set; }

        public string? Name { get; set; }

        public bool Restricted { get; set; }
    }
}

/// <summary>A cached learner model could not be read; retrain from history.</summary>
public sealed class LearnerModelFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LearnerModelFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public LearnerModelFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public LearnerModelFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
