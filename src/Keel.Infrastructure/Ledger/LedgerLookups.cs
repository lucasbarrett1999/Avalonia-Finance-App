using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Shared lookups used inside ledger units of work.</summary>
internal static class LedgerLookups
{
    /// <summary>Payee of system "Starting Balance" transactions.</summary>
    public const string StartingBalancePayee = "Starting Balance";

    /// <summary>Payee of reconciliation balance adjustments.</summary>
    public const string ReconciliationAdjustmentPayee = "Reconciliation Balance Adjustment";

    /// <summary>Loads an account for update or throws <see cref="LedgerError.AccountNotFound"/>.</summary>
    public static async Task<Account> AccountAsync(KeelDbContext db, Guid id, CancellationToken ct) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false)
            ?? throw new LedgerValidationException(LedgerError.AccountNotFound);

    /// <summary>Loads an open account or throws.</summary>
    public static async Task<Account> OpenAccountAsync(KeelDbContext db, Guid id, CancellationToken ct)
    {
        var account = await AccountAsync(db, id, ct).ConfigureAwait(false);
        return account.IsClosed ? throw new LedgerValidationException(LedgerError.AccountClosed) : account;
    }

    /// <summary>Throws unless the category exists.</summary>
    public static async Task EnsureCategoryAsync(KeelDbContext db, Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is { } id && !await db.Categories.AnyAsync(c => c.Id == id, ct).ConfigureAwait(false))
        {
            throw new LedgerValidationException(LedgerError.CategoryNotFound);
        }
    }

    /// <summary>
    /// Returns the tracked or stored payee with this normalized name, adding it to the context if
    /// new (so it is part of the same action and undo entry). Null for an empty name.
    /// </summary>
    public static async Task<Payee?> GetOrAddPayeeAsync(KeelDbContext db, string? name, CancellationToken ct)
    {
        var clean = PayeeNames.Clean(name);
        if (clean.Length == 0)
        {
            return null;
        }

        var normalized = PayeeNames.Normalize(clean);
        var tracked = db.ChangeTracker.Entries<Payee>().Select(e => e.Entity).FirstOrDefault(p => p.NormalizedName == normalized);
        if (tracked is not null)
        {
            return tracked;
        }

        var existing = await db.Payees.SingleOrDefaultAsync(p => p.NormalizedName == normalized, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var payee = new Payee { Name = clean, NormalizedName = normalized };
        db.Payees.Add(payee);
        return payee;
    }

    /// <summary>Category of a system inflow (starting balance, adjustment): Ready to Assign for on-budget assets.</summary>
    public static Guid? SystemInflowCategory(Account account) =>
        account.IsOnBudget && !AccountTypeInfo.IsLiability(account.Type) ? SystemIds.ReadyToAssignCategory : null;

    /// <summary>Maps a split problem to its ledger error.</summary>
    public static LedgerError ToError(SplitProblem problem) => problem switch
    {
        SplitProblem.TooFewLines => LedgerError.SplitTooFewLines,
        SplitProblem.ZeroLine => LedgerError.SplitZeroLine,
        _ => LedgerError.SplitSumMismatch,
    };
}
