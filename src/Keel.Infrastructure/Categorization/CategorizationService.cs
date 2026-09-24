using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain.Categorization;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Domain.Rules;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Categorization;

/// <summary>
/// <see cref="ICategorizationService"/> over the open budget file: stored rules, the cached
/// learner model and payee default categories fed to <see cref="ICategorizationEngine"/>
/// (ADR 0022). Writes go through <see cref="LedgerWriter"/> as one undoable action each.
/// </summary>
public sealed partial class CategorizationService : ICategorizationService
{
    private const int ReadChunk = 500;
    private readonly IDbContextFactory<KeelDbContext> _factory;
    private readonly LedgerWriter _writer;
    private readonly ILearnerService _learner;
    private readonly ICategorizationEngine _engine;
    private readonly ILogger<CategorizationService> _logger;

    /// <summary>Creates the service.</summary>
    public CategorizationService(
        IDbContextFactory<KeelDbContext> factory,
        LedgerWriter writer,
        ILearnerService learner,
        ICategorizationEngine engine,
        ILogger<CategorizationService>? logger = null)
    {
        _factory = factory;
        _writer = writer;
        _learner = learner;
        _engine = engine;
        _logger = logger ?? NullLogger<CategorizationService>.Instance;
    }

    /// <inheritdoc />
    public async Task<TransactionCategorization?> SuggestAsync(Guid transactionId, CancellationToken ct) =>
        (await SuggestManyAsync([transactionId], ct).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransactionCategorization>> SuggestManyAsync(IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);
        var model = await ModelAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            async () =>
            {
                var db = _factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
                    var rules = await CompiledRulesAsync(db, ct).ConfigureAwait(false);
                    var defaults = await PayeeDefaultsAsync(db, source, ct).ConfigureAwait(false);
                    var results = new List<TransactionCategorization>(transactionIds.Count);
                    foreach (var chunk in transactionIds.Distinct().Chunk(ReadChunk))
                    {
                        var ids = chunk.ToList();
                        var rows = await db.Transactions.AsNoTracking().Include(t => t.Splits)
                            .Where(t => ids.Contains(t.Id))
                            .ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
                        foreach (var id in ids)
                        {
                            if (!rows.TryGetValue(id, out var row))
                            {
                                continue;
                            }

                            var snapshot = source.Snapshot(row);

                            // Score a singly categorized row as if it had no category, so its suggestions still show.
                            var input = snapshot.IsSplit || snapshot.TransferAccountId is not null ? snapshot : snapshot with { CategoryId = null };
                            var result = _engine.Categorize(input, rules, model, DefaultFor(row.PayeeId, defaults));
                            results.Add(new TransactionCategorization(id, snapshot, result));
                        }
                    }

                    IReadOnlyList<TransactionCategorization> list = results;
                    return list;
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CategorizationWriteResult> ApplyRulesAsync(IReadOnlyList<Guid> transactionIds, CancellationToken ct) =>
        WriteAsync(transactionIds, model: null, useSuggestions: false, ct);

    /// <inheritdoc />
    public async Task<CategorizationWriteResult> CategorizeAsync(IReadOnlyList<Guid> transactionIds, CancellationToken ct)
    {
        // Load the model before the ledger write starts: the learner reads (and caches) on its own connection.
        var model = await ModelAsync(ct).ConfigureAwait(false);
        return await WriteAsync(transactionIds, model, useSuggestions: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> ApproveAsync(IReadOnlyList<ReviewDecision> decisions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0)
        {
            return Task.FromResult(0);
        }

        return _writer.RunAsync(
            LedgerAction.Approve,
            async session =>
            {
                var db = session.Db;
                var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
                var rules = decisions.Any(d => d.ApplyRules) ? await CompiledRulesAsync(db, ct).ConfigureAwait(false) : null;
                var approved = 0;
                foreach (var chunk in decisions.Chunk(ReadChunk))
                {
                    var ids = chunk.Select(d => d.TransactionId).ToList();
                    var rows = await db.Transactions.Include(t => t.Splits).Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
                    foreach (var decision in chunk)
                    {
                        if (!rows.TryGetValue(decision.TransactionId, out var row))
                        {
                            continue;
                        }

                        var split = row.IsSplit;
                        if (decision.ApplyRules && rules is not null)
                        {
                            var plan = MutationPlanner.Plan(rules.Apply(source.Snapshot(row)), source);
                            await MutationPlanner.WriteAsync(db, row, plan, source, ct).ConfigureAwait(false);
                            split = plan.After.IsSplit;
                        }

                        if (decision.CategoryId is { } category && !split && TakesCategory(row, source))
                        {
                            await LedgerLookups.EnsureCategoryAsync(db, category, ct).ConfigureAwait(false);
                            row.CategoryId = category;
                        }

                        if (!row.IsApproved)
                        {
                            row.IsApproved = true;
                            approved++;
                        }
                    }
                }

                return approved;
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewDecision>> PlanBatchApprovalAsync(double minimumConfidence, CancellationToken ct)
    {
        var ids = await Task.Run(
            async () =>
            {
                var db = _factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    return await db.Transactions.AsNoTracking().Where(t => !t.IsApproved)
                        .OrderBy(t => t.Date).ThenBy(t => t.Id).Select(t => t.Id)
                        .ToListAsync(ct).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);
        var suggestions = await SuggestManyAsync(ids, ct).ConfigureAwait(false);
        return suggestions
            .Where(s => s.Primary is { } p && p.Confidence >= minimumConfidence
                && (s.Snapshot.CategoryId is null || s.Snapshot.CategoryId == p.CategoryId))
            .Select(s => new ReviewDecision(s.TransactionId, s.Primary!.CategoryId, s.Primary.Source == CategorizationSource.Rule))
            .ToList();
    }

    private Task<CategorizationWriteResult> WriteAsync(IReadOnlyList<Guid> transactionIds, LearnerModel? model, bool useSuggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);
        return _writer.RunAsync(
            LedgerAction.ApplyRules,
            async session =>
            {
                var db = session.Db;
                var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
                var rules = await CompiledRulesAsync(db, ct).ConfigureAwait(false);
                var defaults = useSuggestions ? await PayeeDefaultsAsync(db, source, ct).ConfigureAwait(false) : [];
                var plans = new List<PlannedMutation>();
                var examined = 0;
                foreach (var chunk in transactionIds.Distinct().Chunk(ReadChunk))
                {
                    var ids = chunk.ToList();
                    var rows = await db.Transactions.AsNoTracking().Include(t => t.Splits).Where(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        examined++;
                        var snapshot = source.Snapshot(row);
                        var result = _engine.Categorize(snapshot, rules, model, DefaultFor(row.PayeeId, defaults));
                        var plan = MutationPlanner.Plan(result.Rules, source);
                        if (useSuggestions
                            && result.DecidedBy is CategorizationSource.PayeeDefault or CategorizationSource.Learner
                            && result.CategoryId is { } category
                            && plan.After.CategoryId is null
                            && !plan.After.IsSplit
                            && plan.After.TransferAccountId is null)
                        {
                            var after = plan.After with { CategoryId = category };
                            plan = plan with { After = after, Changes = MutationPlanner.Diff(plan.Before, after) };
                        }

                        if (plan.Changes != RuleChanges.None)
                        {
                            plans.Add(plan);
                        }
                    }
                }

                await RuleService.WritePlansAsync(db, plans, source, ct).ConfigureAwait(false);
                return new CategorizationWriteResult(examined, plans.Count);
            },
            ct);
    }

    private async Task<LearnerModel?> ModelAsync(CancellationToken ct)
    {
        try
        {
            return await _learner.GetModelAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Suggestions still come from rules and payee defaults.
            LogLearnerUnavailable(_logger, ex);
            return null;
        }
    }

    private static bool TakesCategory(Transaction row, SnapshotSource source)
    {
        if (row.TransferAccountId is not { } other)
        {
            return true;
        }

        var ownOnBudget = source.Accounts.TryGetValue(row.AccountId, out var own) && own.IsOnBudget;
        var otherOnBudget = source.Accounts.TryGetValue(other, out var o) && o.IsOnBudget;
        return TransferRules.SideRequiresCategory(ownOnBudget, otherOnBudget);
    }

    private static async Task<CompiledRuleSet> CompiledRulesAsync(KeelDbContext db, CancellationToken ct)
    {
        var stored = await db.Rules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
        return RuleEngine.Compile(RuleService.ReadableRules(stored));
    }

    private static async Task<Dictionary<Guid, PayeeDefaultCategory>> PayeeDefaultsAsync(KeelDbContext db, SnapshotSource source, CancellationToken ct)
    {
        var rows = await db.Payees.AsNoTracking()
            .Where(p => p.DefaultCategoryId != null)
            .Select(p => new { p.Id, CategoryId = p.DefaultCategoryId!.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(r => source.Categories.ContainsKey(r.CategoryId))
            .ToDictionary(r => r.Id, r => new PayeeDefaultCategory(r.CategoryId, source.Categories[r.CategoryId].Name));
    }

    private static PayeeDefaultCategory? DefaultFor(Guid? payeeId, Dictionary<Guid, PayeeDefaultCategory> defaults) =>
        payeeId is { } id && defaults.TryGetValue(id, out var value) ? value : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The categorization learner is unavailable; using rules and payee defaults only")]
    private static partial void LogLearnerUnavailable(ILogger logger, Exception exception);
}
