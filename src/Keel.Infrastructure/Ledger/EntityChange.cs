using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Keel.Infrastructure.Ledger;

/// <summary>
/// The before and after state of one entity row within one user action. Snapshots hold every
/// mapped property by name with its CLR value; null means the row did not exist.
/// </summary>
internal sealed record EntityChange(
    Type EntityType,
    string Key,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Audit kind of this change.</summary>
    public AuditEventKind Kind => Before is null ? AuditEventKind.Created : After is null ? AuditEventKind.Deleted : AuditEventKind.Updated;

    /// <summary>The value of <paramref name="property"/> after the change, or before it for deletions.</summary>
    public object? Value(string property) =>
        (After ?? Before) is { } snapshot && snapshot.TryGetValue(property, out var value) ? value : null;

    /// <summary>Both values of <paramref name="property"/> (before, after) that are present.</summary>
    public IEnumerable<object> Values(string property)
    {
        if (Before is not null && Before.TryGetValue(property, out var before) && before is not null)
        {
            yield return before;
        }

        if (After is not null && After.TryGetValue(property, out var after) && after is not null)
        {
            yield return after;
        }
    }

    /// <summary>Builds an audit row for this change.</summary>
    public AuditEvent ToAuditEvent(DateTime at) => new()
    {
        At = at,
        Kind = Kind,
        EntityType = EntityType.Name,
        EntityId = Key,
        BeforeJson = Before is null ? null : JsonSerializer.Serialize(Before, JsonOptions),
        AfterJson = After is null ? null : JsonSerializer.Serialize(After, JsonOptions),
    };

    /// <summary>Captures the pending change of a tracked entry, or null when nothing really changed.</summary>
    public static EntityChange? Capture(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State == EntityState.Modified && !entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
        {
            return null;
        }

        var before = entry.State == EntityState.Added ? null : Snapshot(entry, original: true);
        var after = entry.State == EntityState.Deleted ? null : Snapshot(entry, original: false);
        return new EntityChange(entry.Metadata.ClrType, KeyOf(entry), before, after);
    }

    /// <summary>Merges successive changes of the same rows: first "before", last "after".</summary>
    public static IReadOnlyList<EntityChange> Coalesce(IEnumerable<EntityChange> changes)
    {
        var merged = new Dictionary<(Type, string), EntityChange>();
        var order = new List<(Type, string)>();
        foreach (var change in changes)
        {
            var id = (change.EntityType, change.Key);
            if (merged.TryGetValue(id, out var existing))
            {
                merged[id] = existing with { After = change.After };
            }
            else
            {
                merged[id] = change;
                order.Add(id);
            }
        }

        return order
            .Select(id => merged[id])
            .Where(c => !(c.Before is null && c.After is null) && !SameSnapshot(c.Before, c.After))
            .ToList();
    }

    /// <summary>
    /// Stages <paramref name="target"/> (the state to restore) over <paramref name="current"/> in
    /// <paramref name="db"/>: an insert, a delete, or an update of every changed column.
    /// </summary>
    public static void Stage(DbContext db, Type entityType, IReadOnlyDictionary<string, object?>? current, IReadOnlyDictionary<string, object?>? target)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = Activator.CreateInstance(entityType)
            ?? throw new InvalidOperationException($"Cannot create {entityType.Name}.");
        var entry = db.Entry(entity);
        if (target is null)
        {
            Apply(entry, current!);
            entry.State = EntityState.Deleted;
            return;
        }

        Apply(entry, target);
        if (current is null)
        {
            entry.State = EntityState.Added;
            return;
        }

        entry.State = EntityState.Unchanged;
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey() || !current.TryGetValue(property.Metadata.Name, out var old))
            {
                continue;
            }

            property.OriginalValue = old;
            property.IsModified = !Equals(old, property.CurrentValue);
        }
    }

    private static void Apply(EntityEntry entry, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var property in entry.Properties)
        {
            if (values.TryGetValue(property.Metadata.Name, out var value))
            {
                property.CurrentValue = value;
            }
        }
    }

    private static Dictionary<string, object?> Snapshot(EntityEntry entry, bool original) =>
        entry.Properties.ToDictionary(
            p => p.Metadata.Name,
            p => original ? p.OriginalValue : p.CurrentValue,
            StringComparer.Ordinal);

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey() ?? throw new InvalidOperationException($"{entry.Metadata.Name} has no key.");
        return string.Join('|', key.Properties.Select(p => Format(entry.Property(p.Name).CurrentValue)));
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        Guid g => g.ToString("D"),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool SameSnapshot(IReadOnlyDictionary<string, object?>? a, IReadOnlyDictionary<string, object?>? b) =>
        a is not null && b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Equals(kv.Value, v));
}
