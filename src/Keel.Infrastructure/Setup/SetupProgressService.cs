using Keel.Application.Setup;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Setup;

/// <summary>Computes the Home checklist from the budget file (PRD 9.10); the dismissal lives in its Setting table.</summary>
public sealed class SetupProgressService(IDbContextFactory<KeelDbContext> factory) : ISetupProgressService
{
    /// <summary>Setting key of the dismissed flag.</summary>
    public const string DismissedKey = "setup.checklistDismissed";

    /// <inheritdoc />
    public Task<SetupProgress> GetAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var categories = await db.Categories.AnyAsync(c => !c.IsSystem && c.LinkedAccountId == null, ct).ConfigureAwait(false);
                var accounts = await db.Accounts.AnyAsync(ct).ConfigureAwait(false);
                var assignments = await db.BudgetAssignments.AnyAsync(a => a.Assigned != 0, ct).ConfigureAwait(false);
                var dismissed = await DataFileSettings.GetAsync(db, DismissedKey, false, ct).ConfigureAwait(false);
                return new SetupProgress(categories, accounts, assignments, dismissed);
            }
        },
        ct);

    /// <inheritdoc />
    public Task DismissAsync(CancellationToken ct) => DataFileSettings.SetAsync(factory, DismissedKey, true, ct);
}
