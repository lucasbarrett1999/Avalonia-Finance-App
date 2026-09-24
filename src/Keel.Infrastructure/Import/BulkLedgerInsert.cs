using System.Data.Common;
using Keel.Domain.Entities;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Keel.Infrastructure.Import;

/// <summary>
/// Inserts many new rows of one entity type through a single prepared command inside the current
/// <see cref="LedgerSession"/> transaction, with the same row snapshots and <see cref="AuditEvent"/>
/// rows that <see cref="LedgerSession.SaveAsync"/> would write, so undo and audit work unchanged
/// (ADR 0052). Column names and value conversions come from the EF model, never hand-written.
/// </summary>
internal static class BulkLedgerInsert
{
    /// <summary>
    /// Inserts <paramref name="entities"/> (new rows with client keys), writes their audit rows,
    /// and records the changes on <paramref name="session"/>.
    /// </summary>
    public static async Task InsertAsync<T>(LedgerSession session, IReadOnlyList<T> entities, CancellationToken ct)
        where T : class
    {
        if (entities.Count == 0)
        {
            return;
        }

        var db = session.Db;
        var entityType = db.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");
        var properties = entityType.GetProperties().ToList();
        var changes = new List<EntityChange>(entities.Count);
        var rows = new List<object?[]>(entities.Count);
        foreach (var entity in entities)
        {
            var values = new object?[properties.Count];
            var snapshot = new Dictionary<string, object?>(properties.Count, StringComparer.Ordinal);
            for (var i = 0; i < properties.Count; i++)
            {
                values[i] = properties[i].GetGetter().GetClrValue(entity);
                snapshot[properties[i].Name] = values[i];
            }

            rows.Add(values);
            changes.Add(new EntityChange(typeof(T), Key(entityType, values, properties), null, snapshot));
        }

        await WriteAsync(db, entityType, properties, rows, ct).ConfigureAwait(false);

        var now = session.UtcNow;
        var auditType = db.Model.FindEntityType(typeof(AuditEvent))!;
        var auditProperties = auditType.GetProperties().Where(p => p.ValueGenerated == ValueGenerated.Never).ToList();
        var auditRows = changes.Select(c =>
        {
            var audit = c.ToAuditEvent(now);
            return auditProperties.Select(p => p.GetGetter().GetClrValue(audit)).ToArray();
        }).ToList();
        await WriteAsync(db, auditType, auditProperties, auditRows, ct).ConfigureAwait(false);

        session.AddWrittenChanges(changes);
    }

    private static async Task WriteAsync(KeelDbContext db, IEntityType entityType, List<IProperty> properties, List<object?[]> rows, CancellationToken ct)
    {
        var table = entityType.GetTableName()!;
        var columns = string.Join(", ", properties.Select(p => Quote(p.GetColumnName())));
        var names = properties.Select((_, i) => "@p" + i).ToList();
        var connection = db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = $"INSERT INTO {Quote(table)} ({columns}) VALUES ({string.Join(", ", names)})";
            var mappings = properties.Select(p => (RelationalTypeMapping)p.GetRelationalTypeMapping()).ToList();
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                for (var i = 0; i < properties.Count; i++)
                {
                    command.Parameters.Add(mappings[i].CreateParameter(command, names[i], row[i], properties[i].IsNullable));
                }

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    // The same key text EntityChange.Capture produces (Guid "D", invariant formatting).
    private static string Key(IEntityType entityType, object?[] values, List<IProperty> properties)
    {
        var key = entityType.FindPrimaryKey()!;
        return string.Join('|', key.Properties.Select(p => values[properties.IndexOf((IProperty)p)] switch
        {
            null => string.Empty,
            Guid g => g.ToString("D"),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var v => v.ToString() ?? string.Empty,
        }));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
