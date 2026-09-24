using Keel.Application.Payees;
using Keel.Application.Undo;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Payee lookup and creation.</summary>
public sealed class PayeeService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer) : IPayeeService
{
    /// <inheritdoc />
    public async Task<PayeeDto> GetOrCreateAsync(string name, CancellationToken ct)
    {
        if (PayeeNames.Clean(name).Length == 0)
        {
            throw new ArgumentException("A payee needs a name.", nameof(name));
        }

        var normalized = PayeeNames.Normalize(name);
        var existing = await FindAsync(normalized, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        return await writer.RunAsync(
            LedgerAction.CreatePayee,
            async session =>
            {
                var payee = await LedgerLookups.GetOrAddPayeeAsync(session.Db, name, ct).ConfigureAwait(false);
                return new PayeeDto(payee!.Id, payee.Name, payee.DefaultCategoryId);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PayeeDto>> SearchAsync(string text, int limit, CancellationToken ct) => Task.Run(
        async () =>
        {
            var normalized = PayeeNames.Normalize(text);
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var pattern = "%" + EscapeLike(normalized) + "%";
                var candidates = await db.Payees.AsNoTracking()
                    .Where(p => EF.Functions.Like(p.NormalizedName, pattern, "\\"))
                    .Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.NormalizedName,
                        p.DefaultCategoryId,
                        Uses = db.Transactions.Count(t => t.PayeeId == p.Id),
                    })
                    .ToListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<PayeeDto> result = candidates
                    .OrderByDescending(p => p.NormalizedName.StartsWith(normalized, StringComparison.Ordinal))
                    .ThenByDescending(p => p.Uses)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(Math.Max(1, limit))
                    .Select(p => new PayeeDto(p.Id, p.Name, p.DefaultCategoryId))
                    .ToList();
                return result;
            }
        },
        ct);

    /// <inheritdoc />
    public Task<PayeeSuggestion?> GetSuggestionAsync(string name, Guid? accountId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var normalized = PayeeNames.Normalize(name);
            if (normalized.Length == 0)
            {
                return null;
            }

            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var payee = await db.Payees.AsNoTracking().SingleOrDefaultAsync(p => p.NormalizedName == normalized, ct).ConfigureAwait(false);
                if (payee is null)
                {
                    return null;
                }

                var last = await db.Transactions.AsNoTracking()
                    .Where(t => t.PayeeId == payee.Id)
                    .OrderByDescending(t => accountId != null && t.AccountId == accountId)
                    .ThenByDescending(t => t.Date)
                    .ThenByDescending(t => t.CreatedAt)
                    .Select(t => new { t.CategoryId, t.Memo })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var categoryId = last?.CategoryId ?? payee.DefaultCategoryId;
                var categoryName = categoryId is { } id
                    ? await db.Categories.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).SingleOrDefaultAsync(ct).ConfigureAwait(false)
                    : null;
                return new PayeeSuggestion(payee.Id, payee.Name, categoryId, categoryName, last?.Memo);
            }
        },
        ct);

    private async Task<PayeeDto?> FindAsync(string normalized, CancellationToken ct)
    {
        var db = factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            return await db.Payees.AsNoTracking()
                .Where(p => p.NormalizedName == normalized)
                .Select(p => new PayeeDto(p.Id, p.Name, p.DefaultCategoryId))
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
    }

    internal static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
