using System.Data.Common;
using System.Globalization;
using Keel.Domain.Entities;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Keel.Infrastructure.Portability;

/// <summary>
/// Writes the rows of one bundle table through a single prepared command in the current database
/// transaction, plus one <see cref="AuditEvent"/> per row built exactly as <see cref="EntityChange"/> builds
/// them (ADR 0052 style, ADR 0098). Column names and value conversions come from the EF model. Rows whose key
/// already exists (the rows every new file is seeded with: the default profile, the Inflow and Credit Card
/// Payments groups, Ready to Assign) are updated to the bundle's values instead of inserted.
/// </summary>
internal sealed class BulkTableWriter : IAsyncDisposable
{
    private readonly DbCommand _insert;
    private readonly DbCommand _audit;
    private readonly BundleTable _table;
    private readonly List<RelationalTypeMapping> _mappings;
    private readonly List<IProperty> _auditProperties;
    private readonly List<RelationalTypeMapping> _auditMappings;
    private readonly DateTime _now;
    private readonly int[] _keyIndexes;

    private BulkTableWriter(KeelDbContext db, BundleTable table, DateTime now)
    {
        _table = table;
        _now = now;
        _keyIndexes = table.Key.Select(k => table.Properties.ToList().IndexOf(k)).ToArray();
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var columns = table.Properties.Select(p => Quote(p.GetColumnName())).ToList();
        var keys = table.Key.Select(p => Quote(p.GetColumnName())).ToList();
        var others = columns.Except(keys).ToList();
        var conflict = others.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", others.Select(c => $"{c} = excluded.{c}"));
        _insert = connection.CreateCommand();
        _insert.Transaction = transaction;
        _insert.CommandText = $"INSERT INTO {Quote(table.TableName)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i.ToString(CultureInfo.InvariantCulture)))}) ON CONFLICT ({string.Join(", ", keys)}) {conflict}";
        _mappings = table.Properties.Select(p => (RelationalTypeMapping)p.GetRelationalTypeMapping()).ToList();

        var auditType = db.Model.FindEntityType(typeof(AuditEvent))!;
        _auditProperties = auditType.GetProperties().Where(p => p.ValueGenerated == ValueGenerated.Never).ToList();
        _auditMappings = _auditProperties.Select(p => (RelationalTypeMapping)p.GetRelationalTypeMapping()).ToList();
        _audit = connection.CreateCommand();
        _audit.Transaction = transaction;
        _audit.CommandText = $"INSERT INTO {Quote(auditType.GetTableName()!)} ({string.Join(", ", _auditProperties.Select(p => Quote(p.GetColumnName())))}) VALUES ({string.Join(", ", _auditProperties.Select((_, i) => "@a" + i.ToString(CultureInfo.InvariantCulture)))})";
    }

    /// <summary>Creates a writer for <paramref name="table"/> inside the context's current transaction.</summary>
    public static BulkTableWriter For(KeelDbContext db, BundleTable table, DateTime now) => new(db, table, now);

    /// <summary>Writes one row (values in <see cref="BundleTable.Properties"/> order) and its audit row.</summary>
    public async Task WriteAsync(object?[] values, CancellationToken ct)
    {
        _insert.Parameters.Clear();
        for (var i = 0; i < values.Length; i++)
        {
            _insert.Parameters.Add(_mappings[i].CreateParameter(_insert, "@p" + i.ToString(CultureInfo.InvariantCulture), values[i], _table.Properties[i].IsNullable));
        }

        await _insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var snapshot = new Dictionary<string, object?>(values.Length, StringComparer.Ordinal);
        for (var i = 0; i < values.Length; i++)
        {
            snapshot[_table.Properties[i].Name] = values[i];
        }

        var audit = new EntityChange(_table.EntityType.ClrType, Key(values), null, snapshot).ToAuditEvent(_now);
        _audit.Parameters.Clear();
        for (var i = 0; i < _auditProperties.Count; i++)
        {
            _audit.Parameters.Add(_auditMappings[i].CreateParameter(_audit, "@a" + i.ToString(CultureInfo.InvariantCulture), _auditProperties[i].GetGetter().GetClrValue(audit), _auditProperties[i].IsNullable));
        }

        await _audit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _insert.DisposeAsync().ConfigureAwait(false);
        await _audit.DisposeAsync().ConfigureAwait(false);
    }

    // The same key text EntityChange.Capture produces (Guid "D", ISO dates, invariant formatting).
    private string Key(object?[] values) => string.Join('|', _keyIndexes.Select(i => values[i] switch
    {
        null => string.Empty,
        Guid g => g.ToString("D"),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString() ?? string.Empty,
    }));

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
