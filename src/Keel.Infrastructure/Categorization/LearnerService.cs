using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Keel.Application.Categorization;
using Keel.Application.Files;
using Keel.Domain;
using Keel.Domain.Categorization;
using Keel.Domain.Entities;
using Keel.Domain.Import;
using Keel.Domain.Rules;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Categorization;

/// <summary>
/// Keeps the learner model of the open budget file (ADR 0021, ADR 0027). The model is trained from
/// approved history the first time, cached in the <see cref="Setting"/> table under
/// <see cref="SettingKey"/> with the audit-log position it reflects, and brought up to date by
/// replaying the audit events written since then: for every transaction a later change touched
/// (its own row, its splits, or its payee's name), the example it contributed at that position is
/// removed with <see cref="LearnerModel.WithoutExample"/> and its current example added with
/// <see cref="LearnerModel.WithExample"/>. Approvals, category changes, edits, deletes, undo and
/// redo are all covered because every ledger write is audited. A different
/// <see cref="PayeeNormalizer.Version"/>, model format or cache format rebuilds from history.
/// </summary>
public sealed partial class LearnerService : ILearnerService
{
    /// <summary>Setting key of the cached model.</summary>
    public const string SettingKey = "categorization.learner";

    /// <summary>Version of the cache document (not the model format).</summary>
    public const int CacheFormatVersion = 1;

    /// <summary>More pending audit events than this rebuilds instead of replaying.</summary>
    public const int MaxReplayEvents = 20_000;

    private const string CacheFormatName = "keel.learnerCache";
    private static readonly string[] ReplayedTypes = [nameof(Transaction), nameof(TransactionSplit), nameof(Payee), nameof(Category)];

    private readonly IDbContextFactory<KeelDbContext> _factory;
    private readonly IBudgetFileService _files;
    private readonly ILogger<LearnerService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;
    private LearnerStatus _status;

    /// <summary>Creates the service.</summary>
    public LearnerService(IDbContextFactory<KeelDbContext> factory, IBudgetFileService files, ILogger<LearnerService>? logger = null)
    {
        _factory = factory;
        _files = files;
        _logger = logger ?? NullLogger<LearnerService>.Instance;
    }

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    /// <summary>The key that must match for a cached model to be used: normalizer, model format and cache format versions.</summary>
    public static string VersionKey => string.Create(
        CultureInfo.InvariantCulture,
        $"normalizer-{PayeeNormalizer.Version}.model-{LearnerModelJson.CurrentVersion}.cache-{CacheFormatVersion}");

    /// <inheritdoc />
    public LearnerStatus Status => _status;

    /// <summary>How the last load produced the model (tests and diagnostics).</summary>
    public LearnerLoadKind LastLoad { get; private set; }

    /// <inheritdoc />
    public Task<LearnerModel> GetModelAsync(CancellationToken ct) => RunAsync(rebuild: false, persist: true, ct);

    /// <inheritdoc />
    public Task<LearnerModel> PeekModelAsync(CancellationToken ct) => RunAsync(rebuild: false, persist: false, ct);

    /// <inheritdoc />
    public Task WarmUpAsync(CancellationToken ct) => GetModelAsync(ct);

    /// <inheritdoc />
    public Task<LearnerModel> RebuildAsync(CancellationToken ct) => RunAsync(rebuild: true, persist: true, ct);

    private Task<LearnerModel> RunAsync(bool rebuild, bool persist, CancellationToken ct) => Task.Run(
        async () =>
        {
            State state;
            State? toSave = null;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var path = _files.CurrentPath;
                if (rebuild || _state is null || !string.Equals(_state.Path, path, StringComparison.Ordinal))
                {
                    SetStatus(LearnerStatus.Preparing);
                    _state = rebuild ? await BuildAsync(path, ct).ConfigureAwait(false) : await LoadOrBuildAsync(path, ct).ConfigureAwait(false);
                }

                _state = await CatchUpAsync(_state, ct).ConfigureAwait(false);
                if (persist && _state.Dirty)
                {
                    toSave = _state;
                    _state = _state with { Dirty = false };
                }

                state = _state;
                SetStatus(LearnerStatus.Ready);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogLoadFailed(_logger, ex);
                _state = null;
                SetStatus(LearnerStatus.Failed);
                throw;
            }
            finally
            {
                _gate.Release();
            }

            // Written outside the gate so a caller inside a ledger write (an import hook) never waits on it.
            if (toSave is not null)
            {
                try
                {
                    await SaveCacheAsync(toSave, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSaveFailed(_logger, ex);
                }
            }

            return state.Model;
        },
        ct);

    private void SetStatus(LearnerStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<State> LoadOrBuildAsync(string? path, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey, ct).ConfigureAwait(false);
            if (row is not null && TryReadCache(row.ValueJson, out var model, out var watermark))
            {
                var state = await CatchUpAsync(new State(path, model, watermark, Dirty: false), ct).ConfigureAwait(false);

                // Safety net for writes that bypass the audit log (fixtures, external tools).
                var eligible = await Eligible(db).CountAsync(ct).ConfigureAwait(false);
                if (eligible == state.Model.ExampleCount)
                {
                    LastLoad = LearnerLoadKind.Cache;
                    return state;
                }

                LogCacheStale(_logger, state.Model.ExampleCount, eligible);
            }
        }

        return await BuildAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<State> BuildAsync(string? path, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            // One read transaction: the history and the audit position are the same snapshot.
            var transaction = await BeginReadAsync(db, ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var watermark = await db.AuditEvents.MaxAsync(e => (long?)e.Id, ct).ConfigureAwait(false) ?? 0;
                var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
                var examples = new List<LabeledExample>();
                await foreach (var row in Eligible(db).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
                {
                    examples.Add(Example(source.Snapshot(row), row.CategoryId!.Value, source));
                }

                var model = CategoryLearner.Train(examples, LearnerOptions.Default, Catalog(source));
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                LastLoad = LearnerLoadKind.Built;
                return new State(path, model, watermark, Dirty: true);
            }
        }
    }

    // Replays audit events after the state's watermark (see the class summary).
    private async Task<State> CatchUpAsync(State state, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            State next;
            var transaction = await BeginReadAsync(db, ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var latest = await db.AuditEvents.MaxAsync(e => (long?)e.Id, ct).ConfigureAwait(false) ?? 0;
                if (latest == state.Watermark)
                {
                    return state;
                }

                if (latest < state.Watermark)
                {
                    // The file was replaced by an older copy: the cache describes something else.
                    return await BuildAsync(state.Path, ct).ConfigureAwait(false);
                }

                var pending = await db.AuditEvents.AsNoTracking()
                    .Where(e => e.Id > state.Watermark && e.Id <= latest && ReplayedTypes.Contains(e.EntityType))
                    .CountAsync(ct).ConfigureAwait(false);
                if (pending > MaxReplayEvents)
                {
                    return await BuildAsync(state.Path, ct).ConfigureAwait(false);
                }

                var events = await db.AuditEvents.AsNoTracking()
                    .Where(e => e.Id > state.Watermark && e.Id <= latest && ReplayedTypes.Contains(e.EntityType))
                    .OrderBy(e => e.Id)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (events.Count == 0)
                {
                    return state with { Watermark = latest };
                }

                LearnerModel model;
                try
                {
                    model = await ReplayAsync(db, state.Model, events, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // An example the model never learned: the cache and the ledger disagree.
                    LogReplayFailed(_logger, ex);
                    return await BuildAsync(state.Path, ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                next = new State(state.Path, model, latest, Dirty: true);
            }

            return next;
        }
    }

    private static async Task<LearnerModel> ReplayAsync(KeelDbContext db, LearnerModel model, IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        // The state each touched row had at the watermark is the "before" of its first later event.
        var firstTransaction = new Dictionary<Guid, RowState?>();
        var firstSplit = new Dictionary<Guid, (Guid TransactionId, bool Existed)>();
        var oldPayeeNames = new Dictionary<Guid, string?>();
        var categoriesChanged = false;
        foreach (var e in events)
        {
            if (!Guid.TryParse(e.EntityId, out var id))
            {
                continue;
            }

            switch (e.EntityType)
            {
                case nameof(Transaction):
                    if (!firstTransaction.ContainsKey(id))
                    {
                        firstTransaction[id] = e.BeforeJson is null ? null : RowState.Parse(e.BeforeJson);
                    }

                    break;
                case nameof(TransactionSplit):
                    if (!firstSplit.ContainsKey(id) && ParentOf(e.BeforeJson ?? e.AfterJson) is { } parent)
                    {
                        firstSplit[id] = (parent, e.BeforeJson is not null);
                    }

                    break;
                case nameof(Payee):
                    if (!oldPayeeNames.ContainsKey(id))
                    {
                        oldPayeeNames[id] = e.BeforeJson is null ? null : Text(e.BeforeJson, nameof(Payee.Name));
                    }

                    break;
                case nameof(Category):
                    categoriesChanged = true;
                    break;
            }
        }

        var touched = new HashSet<Guid>(firstTransaction.Keys);
        touched.UnionWith(firstSplit.Values.Select(v => v.TransactionId));
        if (oldPayeeNames.Count > 0)
        {
            var payeeIds = oldPayeeNames.Keys.ToList();
            touched.UnionWith(await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.PayeeId != null && payeeIds.Contains(t.PayeeId.Value))
                .Select(t => t.Id)
                .ToListAsync(ct).ConfigureAwait(false));
        }

        var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
        foreach (var chunk in touched.Chunk(500))
        {
            var ids = chunk.ToList();
            var current = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
            var splits = await db.TransactionSplits.IgnoreQueryFilters().AsNoTracking()
                .Where(s => ids.Contains(s.TransactionId))
                .Select(s => new { s.Id, s.TransactionId })
                .ToListAsync(ct).ConfigureAwait(false);
            var currentSplits = splits.ToLookup(s => s.TransactionId, s => s.Id);

            foreach (var id in ids)
            {
                // At the watermark.
                var oldRow = firstTransaction.TryGetValue(id, out var before)
                    ? before
                    : current.TryGetValue(id, out var unchanged) ? RowState.From(unchanged) : null;
                var oldHasSplits = currentSplits[id].Any(s => !firstSplit.ContainsKey(s))
                    || firstSplit.Any(kv => kv.Value.TransactionId == id && kv.Value.Existed);
                string? oldPayee = null;
                if (oldRow?.PayeeId is { } oldPayeeId)
                {
                    oldPayee = oldPayeeNames.TryGetValue(oldPayeeId, out var renamed) ? renamed : source.PayeeName(oldPayeeId);
                }

                var oldExample = oldRow is not null && oldRow.IsEligible(oldHasSplits) ? oldRow.Example(oldPayee, source) : null;

                // Now.
                var nowRow = current.TryGetValue(id, out var row) ? RowState.From(row) : null;
                var nowExample = nowRow is not null && nowRow.IsEligible(currentSplits[id].Any())
                    ? nowRow.Example(source.PayeeName(nowRow.PayeeId), source)
                    : null;

                if (SameExample(oldExample, nowExample))
                {
                    continue;
                }

                if (oldExample is not null)
                {
                    model = model.WithoutExample(oldExample);
                }

                if (nowExample is not null)
                {
                    model = model.WithExample(nowExample);
                }
            }
        }

        return categoriesChanged ? model.WithCategories(Catalog(source)) : model;
    }

    private static bool SameExample(LabeledExample? a, LabeledExample? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => a.CategoryId == b.CategoryId
                && a.Transaction.AccountId == b.Transaction.AccountId
                && a.Transaction.Date == b.Transaction.Date
                && a.Transaction.Amount == b.Transaction.Amount
                && string.Equals(a.Transaction.EffectivePayee, b.Transaction.EffectivePayee, StringComparison.Ordinal),
        };

    // Approved, categorized, unsplit, non-transfer, non-system history (ADR 0021 consequences).
    private static IQueryable<Transaction> Eligible(KeelDbContext db) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.IsApproved && t.CategoryId != null && t.TransferAccountId == null && t.Source != TransactionSource.System)
            .Where(t => !db.TransactionSplits.Any(s => s.TransactionId == t.Id))
            .OrderBy(t => t.Date).ThenBy(t => t.Id);

    private static LabeledExample Example(TransactionSnapshot snapshot, Guid categoryId, SnapshotSource source) =>
        new(snapshot, categoryId, source.Categories.TryGetValue(categoryId, out var c) ? c.Name : null);

    private static IEnumerable<LearnerCategory> Catalog(SnapshotSource source) =>
        source.Categories.Values.Select(c => new LearnerCategory(c.Id, c.Name, c.IsHidden || c.IsSystem || c.LinkedAccountId is not null));

    private static bool TryReadCache(string json, out LearnerModel model, out long watermark)
    {
        model = null!;
        watermark = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.GetString() != CacheFormatName
                || !root.TryGetProperty("versionKey", out var key) || key.GetString() != VersionKey
                || !root.TryGetProperty("auditWatermark", out var mark) || !mark.TryGetInt64(out watermark)
                || !root.TryGetProperty("model", out var body))
            {
                return false;
            }

            model = LearnerModel.FromJson(body.GetRawText());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or LearnerModelFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The cache document: format, version key, audit position and the model's own JSON.</summary>
    internal static string CacheJson(LearnerModel model, long watermark)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", CacheFormatName);
            writer.WriteNumber("version", CacheFormatVersion);
            writer.WriteString("versionKey", VersionKey);
            writer.WriteNumber("normalizerVersion", PayeeNormalizer.Version);
            writer.WriteNumber("modelVersion", LearnerModelJson.CurrentVersion);
            writer.WriteNumber("auditWatermark", watermark);
            writer.WritePropertyName("model");
            writer.WriteRawValue(model.ToJson(), skipInputValidation: true);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // A derived cache, not user data: written directly (no audit, no undo).
    private async Task SaveCacheAsync(State state, CancellationToken ct)
    {
        var json = CacheJson(state.Model, state.Watermark);
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey, ct).ConfigureAwait(false);
            if (row is null)
            {
                db.Settings.Add(new Setting { Key = SettingKey, ValueJson = json });
            }
            else
            {
                row.ValueJson = json;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    // A deferred transaction: a consistent read snapshot that does not hold the write lock.
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginReadAsync(KeelDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        var transaction = connection.BeginTransaction(deferred: true);
        return await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The read transaction could not be attached.");
    }

    private static Guid? ParentOf(string? json) =>
        json is null ? null : Guid.TryParse(Text(json, nameof(TransactionSplit.TransactionId)), out var id) ? id : null;

    private static string? Text(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the categorization learner failed")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving the categorization learner cache failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Learner cache is stale ({Cached} examples cached, {Eligible} eligible); rebuilding")]
    private static partial void LogCacheStale(ILogger logger, int cached, int eligible);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replaying ledger changes into the learner failed; rebuilding")]
    private static partial void LogReplayFailed(ILogger logger, Exception exception);

    private sealed record State(string? Path, LearnerModel Model, long Watermark, bool Dirty);

    // The learner-relevant columns of a transaction row, from the entity or from audit JSON.
    private sealed record RowState(
        Guid Id,
        Guid AccountId,
        DateOnly Date,
        long Amount,
        Guid? PayeeId,
        string PayeeRaw,
        Guid? CategoryId,
        Guid? TransferAccountId,
        bool IsApproved,
        bool IsDeleted,
        TransactionSource Source)
    {
        public static RowState From(Transaction t) =>
            new(t.Id, t.AccountId, t.Date, t.Amount, t.PayeeId, t.PayeeRaw, t.CategoryId, t.TransferAccountId, t.IsApproved, t.IsDeleted, t.Source);

        public static RowState? Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!Guid.TryParse(Str(r, nameof(Transaction.Id)), out var id)
                || !Guid.TryParse(Str(r, nameof(Transaction.AccountId)), out var account)
                || !DateOnly.TryParseExact(Str(r, nameof(Transaction.Date)), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }

            return new RowState(
                id,
                account,
                date,
                r.TryGetProperty(nameof(Transaction.Amount), out var amount) && amount.TryGetInt64(out var a) ? a : 0,
                OptionalGuid(r, nameof(Transaction.PayeeId)),
                Str(r, nameof(Transaction.PayeeRaw)) ?? string.Empty,
                OptionalGuid(r, nameof(Transaction.CategoryId)),
                OptionalGuid(r, nameof(Transaction.TransferAccountId)),
                Bool(r, nameof(Transaction.IsApproved)),
                Bool(r, nameof(Transaction.IsDeleted)),
                Enum.TryParse<TransactionSource>(Str(r, nameof(Transaction.Source)), out var source) ? source : TransactionSource.Manual);
        }

        public bool IsEligible(bool hasSplits) =>
            IsApproved && !IsDeleted && CategoryId is not null && TransferAccountId is null && Source != TransactionSource.System && !hasSplits;

        public LabeledExample Example(string? payeeName, SnapshotSource source)
        {
            var snapshot = new TransactionSnapshot
            {
                Id = Id,
                AccountId = AccountId,
                Date = Date,
                Amount = Amount,
                PayeeRaw = PayeeRaw,
                Payee = payeeName ?? PayeeRaw,
                PayeeId = PayeeId,
                CategoryId = CategoryId,
                Source = Source,
                IsApproved = IsApproved,
            };
            return LearnerService.Example(snapshot, CategoryId!.Value, source);
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static Guid? OptionalGuid(JsonElement e, string name) => Guid.TryParse(Str(e, name), out var g) ? g : null;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}

/// <summary>How <see cref="LearnerService"/> produced its model.</summary>
public enum LearnerLoadKind
{
    /// <summary>Not loaded yet.</summary>
    None,

    /// <summary>Read from the cache (and caught up).</summary>
    Cache,

    /// <summary>Trained from history.</summary>
    Built,
}
