using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Payees;
using Keel.Application.Undo;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Payee lookup and creation.</summary>
public sealed partial class PayeeService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer, IMessageBus bus) : IPayeeService
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

    /// <inheritdoc />
    public Task<IReadOnlyList<PayeeListItem>> ListAsync(string? search, int limit, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var query = db.Payees.AsNoTracking().Where(p => p.IsTransferPayeeForAccountId == null);
                var normalized = PayeeNames.Normalize(search);
                if (normalized.Length > 0)
                {
                    var pattern = "%" + EscapeLike(normalized) + "%";
                    query = query.Where(p => EF.Functions.Like(p.NormalizedName, pattern, "\\"));
                }

                var rows = await query
                    .OrderBy(p => p.NormalizedName)
                    .Take(Math.Max(1, limit))
                    .Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.DefaultCategoryId,
                        CategoryName = db.Categories.Where(c => c.Id == p.DefaultCategoryId).Select(c => c.Name).FirstOrDefault(),
                        Uses = db.Transactions.Count(t => t.PayeeId == p.Id),
                    })
                    .ToListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<PayeeListItem> result = rows
                    .Select(r => new PayeeListItem(r.Id, r.Name, r.DefaultCategoryId, r.CategoryName, r.Uses))
                    .ToList();
                return result;
            }
        },
        ct);

    /// <inheritdoc />
    public Task SetDefaultCategoryAsync(Guid payeeId, Guid? categoryId, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.UpdatePayee,
            async session =>
            {
                var payee = await session.Db.Payees.SingleOrDefaultAsync(p => p.Id == payeeId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.PayeeNotFound);
                await LedgerLookups.EnsureCategoryAsync(session.Db, categoryId, ct).ConfigureAwait(false);
                payee.DefaultCategoryId = categoryId;
                return true;
            },
            ct);

    /// <inheritdoc />
    public async Task<PayeeDto> RenameAsync(Guid payeeId, string name, CancellationToken ct)
    {
        var clean = PayeeNames.Clean(name);
        if (clean.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.PayeeNameRequired);
        }

        var (result, rules) = await writer.RunAsync(
            LedgerAction.UpdatePayee,
            async session =>
            {
                var db = session.Db;
                var payee = await db.Payees.SingleOrDefaultAsync(p => p.Id == payeeId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.PayeeNotFound);
                var normalized = PayeeNames.Normalize(clean);
                var other = await db.Payees.SingleOrDefaultAsync(p => p.NormalizedName == normalized && p.Id != payeeId, ct).ConfigureAwait(false);
                if (other is null)
                {
                    var oldName = payee.Name;
                    payee.Name = clean;
                    payee.NormalizedName = normalized;
                    var renamedRules = await RenamePayeeInRulesAsync(db, [oldName], clean, ct).ConfigureAwait(false);
                    return (new PayeeDto(payee.Id, payee.Name, payee.DefaultCategoryId), renamedRules);
                }

                // The name is taken: this payee merges into that payee (F-TXN-9 rename is retroactive).
                var counts = await MergeCoreAsync(db, other, [payee], ct).ConfigureAwait(false);
                return (new PayeeDto(other.Id, other.Name, other.DefaultCategoryId), counts.Rules);
            },
            ct).ConfigureAwait(false);
        if (rules > 0)
        {
            bus.Publish(new Keel.Application.Rules.RulesChanged());
        }

        return result;
    }
}
