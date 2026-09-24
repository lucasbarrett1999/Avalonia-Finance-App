using Keel.Application.Forecast;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Forecast;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Keel.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Forecast;

/// <summary>
/// The cash-flow forecast (F-REP-4, ADR 0033) over <see cref="ForecastEngine"/>. Inputs: cleared
/// balances (one <c>GROUP BY</c>), open scheduled transactions, recurring items, and, only with the
/// toggle on, the outflows of the look-back window for the average discretionary spend. Results are
/// cached per day and request; the cache is dropped by <see cref="Invalidate"/> (on
/// <c>LedgerChanged</c>/<c>RecurringChanged</c>) and also whenever the audit log grew, since every
/// ledger, recurring and scheduled write goes through the audited ledger writer (ADR 0035).
/// </summary>
public sealed class ForecastService(IDbContextFactory<KeelDbContext> factory, TimeProvider time) : IForecastService
{
    private const int CacheSize = 8;
    private readonly Lock _gate = new();
    private readonly List<(CacheKey Key, ForecastResult Result, ForecastDto Dto)> _cache = [];

    /// <summary>How many forecasts were computed (not served from the cache); for tests.</summary>
    public int ComputeCount { get; private set; }

    /// <inheritdoc />
    public Task<ForecastDto> GetForecastAsync(ForecastRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(async () => (await ComputeAsync(request, ct).ConfigureAwait(false)).Dto, ct);
    }

    /// <inheritdoc />
    public Task<ForecastDayExplanationDto> ExplainDayAsync(ForecastRequest request, DateOnly date, Guid? accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            async () =>
            {
                var (result, dto) = await ComputeAsync(request, ct).ConfigureAwait(false);
                var explanation = result.Explain(date, accountId);
                return new ForecastDayExplanationDto(
                    explanation.Date,
                    explanation.AccountId,
                    new Money(explanation.Opening, dto.Currency),
                    explanation.Entries.Select(e => new ForecastEntryDto(e.Kind, e.AccountId, e.Label ?? string.Empty, new Money(e.Amount, dto.Currency), e.SourceId, e.IsOverdue, e.DueDate)).ToList(),
                    new Money(explanation.Closing, dto.Currency));
            },
            ct);
    }

    /// <inheritdoc />
    public Task<ForecastSettings> GetSettingsAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await DataFileSettings.GetAsync(db, DataFileSettings.Forecast, ForecastSettings.Default, ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public Task SaveSettingsAsync(ForecastSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Task.Run(() => DataFileSettings.SetAsync(factory, DataFileSettings.Forecast, settings, ct), ct);
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private async Task<(ForecastResult Result, ForecastDto Dto)> ComputeAsync(ForecastRequest request, CancellationToken ct)
    {
        var today = M5Lookups.Today(time);
        var db = factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var version = await db.Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(rowid), 0) AS \"Value\" FROM \"AuditEvents\"").SingleAsync(ct).ConfigureAwait(false);
            var accountKey = string.Join(',', (request.AccountIds ?? []).Order());
            var key = new CacheKey(today, request.Days, request.IncludeDiscretionarySpend, request.Floor?.Amount, accountKey, version);
            lock (_gate)
            {
                var hit = _cache.FindIndex(c => c.Key == key);
                if (hit >= 0)
                {
                    return (_cache[hit].Result, _cache[hit].Dto);
                }
            }

            var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
            var input = await LoadInputAsync(db, today, request, ct).ConfigureAwait(false);
            var result = ForecastEngine.Compute(input, new ForecastOptions(request.Days, request.IncludeDiscretionarySpend, request.Floor?.Amount));
            var dto = ToDto(result, currency);
            lock (_gate)
            {
                ComputeCount++;
                _cache.RemoveAll(c => c.Key.Today != today || c.Key.Version != version);
                _cache.Add((key, result, dto));
                if (_cache.Count > CacheSize)
                {
                    _cache.RemoveAt(0);
                }
            }

            return (result, dto);
        }
    }

    private static async Task<ForecastInput> LoadInputAsync(KeelDbContext db, DateOnly today, ForecastRequest request, CancellationToken ct)
    {
        var selected = request.AccountIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        var cleared = await db.Transactions.AsNoTracking()
            .Where(t => t.Status != TransactionStatus.Uncleared)
            .GroupBy(t => t.AccountId)
            .Select(g => new { AccountId = g.Key, Sum = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(g => g.AccountId, g => g.Sum, ct).ConfigureAwait(false);
        var forecastAccounts = accounts
            .Where(a => selected is null || selected.Contains(a.Id))
            .Select(a => new ForecastAccount(a.Id, a.Name, a.Type, a.IsOnBudget, cleared.GetValueOrDefault(a.Id), a.IsClosed, a.OpeningDate))
            .ToList();

        var schedules = (await db.ScheduledTransactions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .Where(s => !ScheduledTransactionService.IsFinished(s) && Keel.Domain.Scheduling.RecurrenceRule.TryParse(s.RecurrenceRule, out _, out _))
            .ToList();
        var items = await db.RecurringItems.AsNoTracking().Where(i => i.Status != RecurringStatus.Dismissed).ToListAsync(ct).ConfigureAwait(false);
        var payees = await M5Lookups.PayeeNamesAsync(db, schedules.Select(s => s.PayeeId).Concat(items.Select(i => i.PayeeId)), ct).ConfigureAwait(false);

        IReadOnlyList<ForecastHistoryTransaction> history = [];
        if (request.IncludeDiscretionarySpend)
        {
            var from = today.AddDays(-ForecastOptions.Default.DiscretionaryLookbackDays);
            history = await db.Transactions.AsNoTracking()
                .Where(t => t.Date >= from && t.Date < today && t.Amount < 0)
                .Select(t => new ForecastHistoryTransaction(t.Id, t.AccountId, t.Date, t.Amount, t.PayeeId, t.TransferAccountId != null, t.ScheduledFromId))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        return new ForecastInput(
            today,
            forecastAccounts,
            schedules.Select(s => ForecastScheduled.From(s, payees.GetValueOrDefault(s.PayeeId))).ToList(),
            items.Select(i => ForecastRecurring.From(i, payees.GetValueOrDefault(i.PayeeId))).ToList(),
            history);
    }

    private static ForecastDto ToDto(ForecastResult result, string currency)
    {
        var entryDays = result.Entries.Select(e => (e.AccountId, e.Date)).ToHashSet();
        var anyDays = result.Entries.Select(e => e.Date).ToHashSet();
        ForecastSeriesDto Series(ForecastSeries s) => new(
            s.AccountId,
            s.Name,
            s.Days.Select(d => new ForecastPointDto(d.Date, new Money(d.Balance, currency), s.AccountId is { } id ? entryDays.Contains((id, d.Date)) : anyDays.Contains(d.Date))).ToList(),
            s.Lowest.Date,
            new Money(s.Lowest.Balance, currency),
            s.BelowFloor.Select(d => new ForecastPointDto(d.Date, new Money(d.Balance, currency), true)).ToList());
        return new ForecastDto(result.Start, result.End, result.Accounts.Select(Series).ToList(), Series(result.Combined), result.Skipped, result.Discretionary, currency);
    }

    private readonly record struct CacheKey(DateOnly Today, int Days, bool Discretionary, long? Floor, string Accounts, long Version);
}
