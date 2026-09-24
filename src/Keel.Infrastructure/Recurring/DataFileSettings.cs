using System.Text.Json;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Recurring;

/// <summary>
/// Data-file settings (the <see cref="Setting"/> table, one JSON value per key) used by the M5
/// services: subscription designations, re-enabled payees, the last detection run and the forecast
/// floor. Settings are preferences, not ledger rows: they are written directly (no undo entry).
/// </summary>
internal static class DataFileSettings
{
    /// <summary>Category groups designated as subscription groups (Guid[]).</summary>
    public const string SubscriptionGroups = "recurring.subscriptionGroupIds";

    /// <summary>Tags designated as subscription tags (Guid[]).</summary>
    public const string SubscriptionTags = "recurring.subscriptionTagIds";

    /// <summary>Normalized payees the user re-enabled for detection (string[]).</summary>
    public const string ReenabledPayees = "recurring.reenabledPayees";

    /// <summary>Date of the last detection run (yyyy-MM-dd).</summary>
    public const string LastDetection = "recurring.lastDetectionDate";

    /// <summary>Audit-log position up to which imported transactions were run through detection.</summary>
    public const string ImportWatermark = "recurring.importAuditWatermark";

    /// <summary>Forecast floor and discretionary toggle.</summary>
    public const string Forecast = "forecast.settings";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Reads a value, or <paramref name="fallback"/> when missing or unreadable.</summary>
    public static async Task<T> GetAsync<T>(KeelDbContext db, string key, T fallback, CancellationToken ct)
    {
        var json = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.ValueJson).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Stages a value in <paramref name="db"/> (the caller saves).</summary>
    public static async Task StageAsync<T>(KeelDbContext db, string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, Options);
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        }
        else
        {
            row.ValueJson = json;
        }
    }

    /// <summary>Writes a value in its own short-lived context.</summary>
    public static async Task SetAsync<T>(IDbContextFactory<KeelDbContext> factory, string key, T value, CancellationToken ct)
    {
        var db = factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            await StageAsync(db, key, value, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Lookups shared by the M5 services.</summary>
internal static class M5Lookups
{
    /// <summary>The user's local date (the ledger has no time component).</summary>
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    /// <summary>The budget's currency: that of the first on-budget account (PRD D3, as in reports).</summary>
    public static async Task<string> CurrencyAsync(KeelDbContext db, CancellationToken ct) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.IsOnBudget)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .Select(a => a.Currency)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? Currency.Default;

    /// <summary>Payee names by id.</summary>
    public static async Task<Dictionary<Guid, string>> PayeeNamesAsync(KeelDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var set = ids.Distinct().ToList();
        return set.Count == 0
            ? []
            : await db.Payees.AsNoTracking().Where(p => set.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct).ConfigureAwait(false);
    }
}
