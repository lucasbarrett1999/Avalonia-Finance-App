using System.Globalization;
using System.Text.Json;
using Keel.Domain.Entities;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Keel.Infrastructure.Portability;

/// <summary>One table of the bundle: an EF entity type and its mapped properties in a fixed order.</summary>
/// <param name="EntityType">The EF entity type.</param>
/// <param name="Properties">Every mapped property, key properties first (EF's order).</param>
internal sealed record BundleTable(IEntityType EntityType, IReadOnlyList<IProperty> Properties)
{
    /// <summary>The name used in the bundle: the entity's CLR name (<c>Transaction</c>, <c>BudgetAssignment</c>).</summary>
    public string Name => EntityType.ClrType.Name;

    /// <summary>The SQLite table.</summary>
    public string TableName => EntityType.GetTableName()!;

    /// <summary>The primary key properties.</summary>
    public IReadOnlyList<IProperty> Key => EntityType.FindPrimaryKey()!.Properties;
}

/// <summary>
/// What the JSON bundle holds and how values are written (ADR 0098). The tables come from the EF model, so
/// a table or column added by a later migration is exported and imported without changes here: every
/// entity type except <see cref="AuditEvent"/> (the undo and activity history of the source file, which
/// the import rebuilds with its own audit rows), in foreign-key order. Setting rows that cache positions
/// in the source file's audit log (the learner cache, the recurring watermark) are left out; the new file
/// rebuilds them.
/// </summary>
internal static class BundleSchema
{
    /// <summary>Setting keys that are derived from the source file's audit log and never exported.</summary>
    public static IReadOnlySet<string> ExcludedSettingKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        LearnerService.SettingKey,
        DataFileSettings.ImportWatermark,
    };

    /// <summary>The bundle's tables in foreign-key order (principal before dependent; ties by name).</summary>
    public static IReadOnlyList<BundleTable> Tables(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var types = model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.GetTableName() is not null && t.ClrType != typeof(AuditEvent))
            .OrderBy(t => t.ClrType.Name, StringComparer.Ordinal)
            .ToList();
        var ordered = new List<IEntityType>();
        var remaining = types.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(t => t.GetForeignKeys()
                .Select(fk => fk.PrincipalEntityType)
                .All(p => p == t || !remaining.Contains(p)));
            next ??= remaining[0]; // a cycle: the import defers foreign keys, so any order is valid
            ordered.Add(next);
            remaining.Remove(next);
        }

        return ordered.Select(t => new BundleTable(t, t.GetProperties().ToList())).ToList();
    }

    /// <summary>All rows of <paramref name="table"/> ordered by key, streamed (deleted transactions included).</summary>
    public static IAsyncEnumerable<object> Rows(KeelDbContext db, BundleTable table) =>
        (IAsyncEnumerable<object>)typeof(BundleSchema).GetMethod(nameof(RowsOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(table.EntityType.ClrType)
            .Invoke(null, [db, table])!;

    /// <summary>The number of rows <see cref="Rows"/> returns.</summary>
    public static Task<int> CountAsync(KeelDbContext db, BundleTable table, CancellationToken ct) =>
        (Task<int>)typeof(BundleSchema).GetMethod(nameof(CountOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(table.EntityType.ClrType)
            .Invoke(null, [db, ct])!;

    /// <summary>Whether a row is exported (every row but the excluded setting keys).</summary>
    public static bool IsExported(object row) => row is not Setting setting || !ExcludedSettingKeys.Contains(setting.Key);

    /// <summary>Writes a CLR value of a mapped property.</summary>
    public static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString("D"));
                break;
            case DateOnly d:
                writer.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case DateTime t:
                writer.WriteStringValue(DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
                break;
            case Enum e:
                writer.WriteStringValue(e.ToString());
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case short h:
                writer.WriteNumberValue(h);
                break;
            case double f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                throw new NotSupportedException($"Values of type {value.GetType().Name} are not supported in bundles.");
        }
    }

    /// <summary>Reads a token as a value of <paramref name="type"/>; false when the token does not fit.</summary>
    public static bool TryReadValue(JsonTokenStream json, Type type, out object? value)
    {
        ArgumentNullException.ThrowIfNull(json);
        value = null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (json.TokenType == JsonTokenType.Null)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        }

        var text = json.Text;
        try
        {
            switch (json.TokenType)
            {
                case JsonTokenType.True or JsonTokenType.False when target == typeof(bool):
                    value = json.TokenType == JsonTokenType.True;
                    return true;
                case JsonTokenType.String when target == typeof(string):
                    value = text;
                    return true;
                case JsonTokenType.String when target == typeof(Guid):
                    value = Guid.ParseExact(text!, "D");
                    return true;
                case JsonTokenType.String when target == typeof(DateOnly):
                    value = DateOnly.ParseExact(text!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return true;
                case JsonTokenType.String when target == typeof(DateTime):
                    value = DateTime.SpecifyKind(DateTime.Parse(text!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
                    return true;
                case JsonTokenType.String when target.IsEnum:
                    if (!Enum.GetNames(target).Contains(text, StringComparer.Ordinal))
                    {
                        return false;
                    }

                    value = Enum.Parse(target, text!, ignoreCase: false);
                    return true;
                case JsonTokenType.String when target == typeof(byte[]):
                    value = Convert.FromBase64String(text!);
                    return true;
                case JsonTokenType.Number when target == typeof(long):
                    value = long.Parse(text!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return true;
                case JsonTokenType.Number when target == typeof(int):
                    value = int.Parse(text!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return true;
                case JsonTokenType.Number when target == typeof(short):
                    value = short.Parse(text!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return true;
                case JsonTokenType.Number when target == typeof(double):
                    value = double.Parse(text!, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return true;
                case JsonTokenType.Number when target == typeof(decimal):
                    value = decimal.Parse(text!, NumberStyles.Float, CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static async IAsyncEnumerable<object> RowsOf<T>(KeelDbContext db, BundleTable table)
        where T : class
    {
        IQueryable<T> query = db.Set<T>().IgnoreQueryFilters().AsNoTracking();
        IOrderedQueryable<T>? ordered = null;
        foreach (var key in table.Key)
        {
            var name = key.Name;
            ordered = ordered is null ? query.OrderBy(e => EF.Property<object>(e, name)) : ordered.ThenBy(e => EF.Property<object>(e, name));
        }

        await foreach (var row in (ordered ?? query).AsAsyncEnumerable().ConfigureAwait(false))
        {
            if (IsExported(row))
            {
                yield return row;
            }
        }
    }

    private static Task<int> CountOf<T>(KeelDbContext db, CancellationToken ct)
        where T : class
    {
        if (typeof(T) == typeof(Setting))
        {
            var excluded = ExcludedSettingKeys.ToList();
            return db.Settings.CountAsync(s => !excluded.Contains(s.Key), ct);
        }

        return db.Set<T>().IgnoreQueryFilters().CountAsync(ct);
    }
}
