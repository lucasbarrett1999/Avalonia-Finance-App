using Keel.Application.Import;

namespace Keel.Infrastructure.Import;

/// <summary>
/// The default <see cref="IImportCategorizationHook"/> until the rules engine and learner (M4)
/// register theirs: payees keep the pipeline's name and the payee's default category applies.
/// </summary>
public sealed class NoOpImportCategorizationHook : IImportCategorizationHook
{
    /// <inheritdoc />
    public ValueTask RenamePayeesAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask CategorizeAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct) => ValueTask.CompletedTask;
}
