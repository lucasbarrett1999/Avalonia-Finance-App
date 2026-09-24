using Keel.Application.Categorization;
using Keel.Application.Import;
using Keel.Domain.Categorization;
using Keel.Domain.Rules;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Categorization;

/// <summary>
/// The rules engine and learner in the import pipeline (F-TXN-1 steps 3 and 4, ADR 0022, ADR 0025).
/// Step 3 applies rules' payee renames to the drafts; step 4 runs <see cref="ICategorizationEngine"/>
/// with the stored rules, the payee default the pipeline resolved (0.95) and the learner model
/// (primary suggestion at least 0.60) and sets the category, memo, approval and reason. It only
/// reads: its own short-lived context and <see cref="ILearnerService.PeekModelAsync"/>, which never
/// writes. Tags, flags, splits and transfers set by rules cannot be expressed on a draft; they apply
/// when rules are applied to the imported rows later (review queue or retroactive apply).
/// </summary>
public sealed partial class RulesImportCategorizationHook : IImportCategorizationHook
{
    private readonly IDbContextFactory<KeelDbContext> _factory;
    private readonly ILearnerService _learner;
    private readonly ICategorizationEngine _engine;
    private readonly ILogger<RulesImportCategorizationHook> _logger;

    /// <summary>Creates the hook.</summary>
    public RulesImportCategorizationHook(
        IDbContextFactory<KeelDbContext> factory,
        ILearnerService learner,
        ICategorizationEngine engine,
        ILogger<RulesImportCategorizationHook>? logger = null)
    {
        _factory = factory;
        _learner = learner;
        _engine = engine;
        _logger = logger ?? NullLogger<RulesImportCategorizationHook>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask RenamePayeesAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
        {
            return;
        }

        var (rules, _) = await ReadAsync(ct).ConfigureAwait(false);
        if (rules.Rules.Count == 0)
        {
            return;
        }

        foreach (var draft in drafts)
        {
            var result = rules.Apply(Snapshot(context, draft, draft.Incoming.CategoryId));
            if (result.Mutations.Has(RuleChanges.Payee) && !string.IsNullOrWhiteSpace(result.Result.Payee))
            {
                draft.PayeeName = result.Result.Payee.Trim();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask CategorizeAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
        {
            return;
        }

        var (rules, categoryNames) = await ReadAsync(ct).ConfigureAwait(false);
        LearnerModel? model = null;
        try
        {
            model = await _learner.PeekModelAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLearnerUnavailable(_logger, ex);
        }

        foreach (var draft in drafts)
        {
            // A category the pipeline filled in from the payee is the payee default, not the source's choice.
            var fromSource = draft.Incoming.CategoryId;
            PayeeDefaultCategory? payeeDefault = fromSource is null && draft.CategoryId is { } d && categoryNames.TryGetValue(d, out var name)
                ? new PayeeDefaultCategory(d, name)
                : null;
            var result = _engine.Categorize(Snapshot(context, draft, fromSource), rules, model, payeeDefault);
            var mutations = result.Rules.Mutations;
            if (mutations.Has(RuleChanges.Memo))
            {
                draft.Memo = string.IsNullOrWhiteSpace(result.Rules.Result.Memo) ? null : result.Rules.Result.Memo;
            }

            if (mutations.Has(RuleChanges.Approved))
            {
                draft.IsApproved = true;
            }

            switch (result.DecidedBy)
            {
                case CategorizationSource.Rule when result.Rules.Result.CategoryId is { } ruleCategory:
                    draft.CategoryId = ruleCategory;
                    draft.CategoryReason = result.Trace.Summary;
                    break;
                case CategorizationSource.PayeeDefault or CategorizationSource.Learner when context.IsOnBudget:
                    draft.CategoryId = result.CategoryId;
                    draft.CategoryReason = result.Trace.Summary;
                    break;
            }
        }
    }

    private static TransactionSnapshot Snapshot(ImportHookContext context, ImportDraft draft, Guid? categoryId) => new()
    {
        AccountId = context.AccountId,
        Date = draft.Incoming.Date,
        Amount = draft.Incoming.Amount,
        PayeeRaw = draft.Incoming.PayeeRaw,
        Payee = draft.PayeeName,
        Memo = draft.Memo,
        CategoryId = categoryId,
        Source = context.Source,
    };

    private async Task<(CompiledRuleSet Rules, Dictionary<Guid, string> Categories)> ReadAsync(CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var stored = await db.Rules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
            var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
            return (RuleEngine.Compile(RuleService.ReadableRules(stored)), categories);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The categorization learner is unavailable during import; using rules and payee defaults only")]
    private static partial void LogLearnerUnavailable(ILogger logger, Exception exception);
}
