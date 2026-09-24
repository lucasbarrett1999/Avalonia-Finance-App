using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Rules;
using Keel.Application.Undo;
using Keel.Domain.Entities;
using Keel.Domain.Rules;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Rules;

/// <summary>
/// Rules persisted as <see cref="RuleJson"/> in the <see cref="Rule"/> table (F-TXN-4, ADR 0020,
/// ADR 0026). Mutations run through <see cref="LedgerWriter"/> (audited, undoable) and publish
/// <see cref="RulesChanged"/>.
/// </summary>
public sealed class RuleService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer, IMessageBus bus) : IRuleService
{
    private const int WriteChunk = 500;

    /// <inheritdoc />
    public Task<IReadOnlyList<RuleDto>> GetRulesAsync(CancellationToken ct) => ReadAsync(
        async (db, source) =>
        {
            var rules = await db.Rules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<RuleDto> result = rules.Select(r => ToDto(r, source)).ToList();
            return result;
        },
        ct);

    /// <inheritdoc />
    public Task<RuleDto?> GetAsync(Guid id, CancellationToken ct) => ReadAsync(
        async (db, source) =>
        {
            var rule = await db.Rules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
            return rule is null ? null : ToDto(rule, source);
        },
        ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RuleProblem>> ValidateAsync(RuleDefinition rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return ReadAsync((_, source) => Task.FromResult(RuleValidator.Validate(rule, source.ValidationContext())), ct);
    }

    /// <inheritdoc />
    public async Task<RuleDto> SaveAsync(RuleDefinition rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var problems = await ValidateAsync(rule, ct).ConfigureAwait(false);
        if (problems.Any(p => p.Severity == RuleProblemSeverity.Error))
        {
            throw new RuleValidationException(problems);
        }

        var clean = rule with { Name = rule.Name.Trim() };
        var id = await writer.RunAsync(
            LedgerAction.SaveRule,
            async session =>
            {
                var rules = session.Db.Rules;
                if (clean.Id == Guid.Empty)
                {
                    var last = await rules.MaxAsync(r => (int?)r.SortOrder, ct).ConfigureAwait(false);
                    var entity = (clean with { SortOrder = (last ?? -1) + 1 }).ToEntity();
                    rules.Add(entity);
                    return entity.Id;
                }

                var existing = await rules.SingleOrDefaultAsync(r => r.Id == clean.Id, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.RuleNotFound);
                var updated = clean.ToEntity();
                existing.Name = updated.Name;
                existing.IsEnabled = updated.IsEnabled;
                existing.ContinueAfterMatch = updated.ContinueAfterMatch;
                existing.ConditionsJson = updated.ConditionsJson;
                existing.ActionsJson = updated.ActionsJson;
                return existing.Id;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RulesChanged());
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await writer.RunAsync(
            LedgerAction.DeleteRule,
            async session =>
            {
                var rule = await session.Db.Rules.SingleOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.RuleNotFound);
                session.Db.Rules.Remove(rule);
                return true;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RulesChanged());
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await writer.RunAsync(
            LedgerAction.SaveRule,
            async session =>
            {
                var rule = await session.Db.Rules.SingleOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.RuleNotFound);
                rule.IsEnabled = enabled;
                return true;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RulesChanged());
    }

    /// <inheritdoc />
    public async Task MoveAsync(Guid id, int delta, CancellationToken ct)
    {
        var changed = await writer.RunAsync(
            LedgerAction.ReorderRules,
            async session =>
            {
                var rules = await OrderedAsync(session.Db, ct).ConfigureAwait(false);
                var index = rules.FindIndex(r => r.Id == id);
                if (index < 0)
                {
                    throw new LedgerValidationException(LedgerError.RuleNotFound);
                }

                var target = Math.Clamp(index + delta, 0, rules.Count - 1);
                if (target == index)
                {
                    return false;
                }

                var rule = rules[index];
                rules.RemoveAt(index);
                rules.Insert(target, rule);
                Renumber(rules);
                return true;
            },
            ct).ConfigureAwait(false);
        if (changed)
        {
            bus.Publish(new RulesChanged());
        }
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        await writer.RunAsync(
            LedgerAction.ReorderRules,
            async session =>
            {
                var rules = await OrderedAsync(session.Db, ct).ConfigureAwait(false);
                var position = orderedIds.Select((id, i) => (id, i)).GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First().i);
                if (position.Keys.Any(id => rules.All(r => r.Id != id)))
                {
                    throw new LedgerValidationException(LedgerError.RuleNotFound);
                }

                // Listed rules in the given order; any others keep their relative order after them.
                var ordered = rules
                    .Select((rule, i) => (rule, key: position.TryGetValue(rule.Id, out var p) ? p : orderedIds.Count + i))
                    .OrderBy(x => x.key)
                    .Select(x => x.rule)
                    .ToList();
                Renumber(ordered);
                return true;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RulesChanged());
    }

    /// <inheritdoc />
    public Task<int> CountMatchesAsync(RuleDefinition rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var compiled = RuleEngine.Compile([rule with { IsEnabled = true, ContinueAfterMatch = false }]);
        return ReadAsync(
            async (db, source) =>
            {
                var count = 0;
                await foreach (var snapshot in source.StreamAsync(SnapshotSource.InScope(db, RetroactiveScope.All), ct).ConfigureAwait(false))
                {
                    if (compiled.Apply(snapshot).Trace.Matched.Count > 0)
                    {
                        count++;
                    }
                }

                return count;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<RuleTestResult> TestAsync(RuleDefinition rule, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var compiled = RuleEngine.Compile([rule with { IsEnabled = true, ContinueAfterMatch = false }]);
        return ReadAsync(
            async (db, source) =>
            {
                var examined = 0;
                var matched = 0;
                var newest = new Queue<PlannedMutation>();
                await foreach (var snapshot in source.StreamAsync(SnapshotSource.InScope(db, RetroactiveScope.All), ct).ConfigureAwait(false))
                {
                    examined++;
                    var application = compiled.Apply(snapshot);
                    if (application.Trace.Matched.Count == 0)
                    {
                        continue;
                    }

                    matched++;
                    newest.Enqueue(MutationPlanner.Plan(application, source));
                    if (newest.Count > Math.Max(0, limit))
                    {
                        newest.Dequeue();
                    }
                }

                var samples = newest.Reverse().Select(p => MutationPlanner.Preview(p, source)).ToList();
                return new RuleTestResult(examined, matched, samples);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<RetroactivePreview> PreviewRetroactiveAsync(IReadOnlyList<Guid> ruleIds, RetroactiveScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        ArgumentNullException.ThrowIfNull(scope);
        return ReadAsync(
            async (db, source) =>
            {
                var (examined, plans) = await PlanRetroactiveAsync(db, source, ruleIds, scope, ct).ConfigureAwait(false);
                return new RetroactivePreview(examined, plans.Select(p => MutationPlanner.Preview(p, source)).ToList());
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<RetroactiveResult> ApplyRetroactivelyAsync(IReadOnlyList<Guid> ruleIds, RetroactiveScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ruleIds);
        ArgumentNullException.ThrowIfNull(scope);
        var result = await writer.RunAsync(
            LedgerAction.ApplyRules,
            async session =>
            {
                var db = session.Db;
                var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
                var (examined, plans) = await PlanRetroactiveAsync(db, source, ruleIds, scope, ct).ConfigureAwait(false);
                await WritePlansAsync(db, plans, source, ct).ConfigureAwait(false);
                return new RetroactiveResult(examined, plans.Count, plans.Select(p => MutationPlanner.Preview(p, source)).ToList());
            },
            ct).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public Task<RuleDefinition> SuggestFromTransactionAsync(Guid transactionId, CancellationToken ct) => ReadAsync(
        async (db, source) =>
        {
            var transaction = await db.Transactions.AsNoTracking().Include(t => t.Splits).SingleOrDefaultAsync(t => t.Id == transactionId, ct).ConfigureAwait(false)
                ?? throw new LedgerValidationException(LedgerError.TransactionNotFound);
            var snapshot = source.Snapshot(transaction);
            return RuleSuggester.FromTransaction(snapshot, source.Names(snapshot.Currency));
        },
        ct);

    /// <inheritdoc />
    public Task<RuleNames> GetNamesAsync(CancellationToken ct) => ReadAsync((_, source) => Task.FromResult(source.Names()), ct);

    /// <summary>Writes plans onto tracked rows, loading them in chunks (shared by retroactive apply and categorization).</summary>
    internal static async Task WritePlansAsync(KeelDbContext db, IReadOnlyList<PlannedMutation> plans, SnapshotSource source, CancellationToken ct)
    {
        foreach (var chunk in plans.Chunk(WriteChunk))
        {
            var ids = chunk.Select(p => p.TransactionId).ToList();
            var rows = await db.Transactions.Include(t => t.Splits).Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
            foreach (var plan in chunk)
            {
                if (rows.TryGetValue(plan.TransactionId, out var row))
                {
                    await MutationPlanner.WriteAsync(db, row, plan, source, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<(int Examined, List<PlannedMutation> Plans)> PlanRetroactiveAsync(
        KeelDbContext db, SnapshotSource source, IReadOnlyList<Guid> ruleIds, RetroactiveScope scope, CancellationToken ct)
    {
        var wanted = ruleIds.ToHashSet();
        var stored = await db.Rules.AsNoTracking().Where(r => wanted.Contains(r.Id)).OrderBy(r => r.SortOrder).ToListAsync(ct).ConfigureAwait(false);
        if (stored.Count != wanted.Count)
        {
            throw new LedgerValidationException(LedgerError.RuleNotFound);
        }

        var compiled = RuleEngine.Compile(ReadableRules(stored));
        var examined = 0;
        var plans = new List<PlannedMutation>();
        await foreach (var snapshot in source.StreamAsync(SnapshotSource.InScope(db, scope), ct).ConfigureAwait(false))
        {
            examined++;
            var plan = MutationPlanner.Plan(compiled.Apply(snapshot), source);
            if (plan.Changes != RuleChanges.None)
            {
                plans.Add(plan);
            }
        }

        return (examined, plans);
    }

    /// <summary>The rules this version can read (unreadable ones are skipped, never rewritten).</summary>
    internal static IEnumerable<RuleDefinition> ReadableRules(IEnumerable<Rule> rules)
    {
        foreach (var rule in rules)
        {
            RuleDefinition? definition;
            try
            {
                definition = RuleDefinition.FromEntity(rule);
            }
            catch (RuleFormatException)
            {
                definition = null;
            }

            if (definition is not null)
            {
                yield return definition;
            }
        }
    }

    private static async Task<List<Rule>> OrderedAsync(KeelDbContext db, CancellationToken ct) =>
        await db.Rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);

    private static void Renumber(List<Rule> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SortOrder != i)
            {
                ordered[i].SortOrder = i;
            }
        }
    }

    private static RuleDto ToDto(Rule rule, SnapshotSource source)
    {
        try
        {
            var definition = RuleDefinition.FromEntity(rule);
            var problems = RuleValidator.Validate(definition, source.ValidationContext());
            return new RuleDto(rule.Id, rule.Name, rule.SortOrder, rule.IsEnabled, rule.ContinueAfterMatch, definition, definition.Describe(source.Names()), null, problems);
        }
        catch (RuleFormatException ex)
        {
            return new RuleDto(rule.Id, rule.Name, rule.SortOrder, rule.IsEnabled, rule.ContinueAfterMatch, null, string.Empty, ex.Message, []);
        }
    }

    private Task<T> ReadAsync<T>(Func<KeelDbContext, SnapshotSource, Task<T>> read, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var source = await SnapshotSource.LoadAsync(db, ct).ConfigureAwait(false);
                return await read(db, source).ConfigureAwait(false);
            }
        },
        ct);
}
