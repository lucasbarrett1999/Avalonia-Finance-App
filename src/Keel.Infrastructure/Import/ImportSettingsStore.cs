using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Application.Import;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Import;

/// <summary>
/// Per-account import memory in the budget file's <c>Setting</c> table (ADR 0051): keys
/// <c>import.csvMapping.{accountId}</c> and <c>import.lastFolder.{accountId}</c>, JSON values.
/// These are preferences, not ledger data: saved directly, not audited, not undone.
/// </summary>
public sealed class ImportSettingsStore(IDbContextFactory<KeelDbContext> factory) : IImportSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Setting key of an account's CSV mapping.</summary>
    public static string MappingKey(Guid accountId) => string.Create(CultureInfo.InvariantCulture, $"import.csvMapping.{accountId:D}");

    /// <summary>Setting key of an account's last import folder.</summary>
    public static string FolderKey(Guid accountId) => string.Create(CultureInfo.InvariantCulture, $"import.lastFolder.{accountId:D}");

    /// <inheritdoc />
    public async Task<RememberedCsvMapping?> GetCsvMappingAsync(Guid accountId, CancellationToken ct)
    {
        var json = await ReadAsync(MappingKey(accountId), ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RememberedCsvMapping>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // An unreadable value (older or hand-edited) is treated as "nothing remembered".
            return null;
        }
    }

    /// <inheritdoc />
    public Task SaveCsvMappingAsync(Guid accountId, RememberedCsvMapping mapping, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return WriteAsync(MappingKey(accountId), JsonSerializer.Serialize(mapping, JsonOptions), ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetLastFolderAsync(Guid accountId, CancellationToken ct)
    {
        var json = await ReadAsync(FolderKey(accountId), ct).ConfigureAwait(false);
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<string>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task SaveLastFolderAsync(Guid accountId, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return WriteAsync(FolderKey(accountId), JsonSerializer.Serialize(folder), ct);
    }

    private Task<string?> ReadAsync(string key, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.ValueJson).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            }
        },
        ct);

    private Task WriteAsync(string key, string json, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
                if (setting is null)
                {
                    db.Settings.Add(new Setting { Key = key, ValueJson = json });
                }
                else
                {
                    setting.ValueJson = json;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        },
        ct);
}
