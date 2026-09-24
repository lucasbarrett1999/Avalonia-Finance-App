using Keel.Domain.Rules;

namespace Keel.Application.Rules;

/// <summary>
/// Stored categorization rules (F-TXN-4): create, edit, order, enable, delete, test and apply
/// retroactively. Every mutation validates with <see cref="RuleValidator"/>, runs as one audited,
/// undoable ledger action, and publishes <see cref="RulesChanged"/>.
/// </summary>
public interface IRuleService
{
    /// <summary>All rules in evaluation order, including unreadable ones (with <see cref="RuleDto.FormatError"/>).</summary>
    Task<IReadOnlyList<RuleDto>> GetRulesAsync(CancellationToken ct);

    /// <summary>One rule, or null.</summary>
    Task<RuleDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Validates a rule against the current categories and accounts (errors and warnings).</summary>
    Task<IReadOnlyList<RuleProblem>> ValidateAsync(RuleDefinition rule, CancellationToken ct);

    /// <summary>
    /// Creates (when <see cref="RuleDefinition.Id"/> is empty; the rule goes last) or updates a rule.
    /// Throws <see cref="RuleValidationException"/> when the validator reports errors.
    /// </summary>
    Task<RuleDto> SaveAsync(RuleDefinition rule, CancellationToken ct);

    /// <summary>Deletes a rule.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Enables or disables a rule.</summary>
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct);

    /// <summary>Moves a rule up (negative <paramref name="delta"/>) or down in the evaluation order.</summary>
    Task MoveAsync(Guid id, int delta, CancellationToken ct);

    /// <summary>Persists a complete order (drag and drop): the rules get sort orders 0, 1, 2, ... in this order.</summary>
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct);

    /// <summary>How many existing transactions the rule's conditions match (computed on demand).</summary>
    Task<int> CountMatchesAsync(RuleDefinition rule, CancellationToken ct);

    /// <summary>"Test rule": the transactions a (possibly unsaved) rule matches, newest first, with what it would change.</summary>
    Task<RuleTestResult> TestAsync(RuleDefinition rule, int limit, CancellationToken ct);

    /// <summary>
    /// Preview of applying stored rules retroactively: every transaction in <paramref name="scope"/>
    /// the rules would change, and the fields it would receive. Nothing is written.
    /// </summary>
    Task<RetroactivePreview> PreviewRetroactiveAsync(IReadOnlyList<Guid> ruleIds, RetroactiveScope scope, CancellationToken ct);

    /// <summary>
    /// Applies stored rules retroactively as one undoable action. Writes exactly what
    /// <see cref="PreviewRetroactiveAsync"/> reports for the same ledger state.
    /// </summary>
    Task<RetroactiveResult> ApplyRetroactivelyAsync(IReadOnlyList<Guid> ruleIds, RetroactiveScope scope, CancellationToken ct);

    /// <summary>"Create rule from this transaction": the prefilled, unsaved rule (<see cref="RuleSuggester"/>).</summary>
    Task<RuleDefinition> SuggestFromTransactionAsync(Guid transactionId, CancellationToken ct);

    /// <summary>Category and account names for describing rules and traces.</summary>
    Task<RuleNames> GetNamesAsync(CancellationToken ct);
}
