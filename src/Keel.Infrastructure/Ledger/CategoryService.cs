using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Category lookup for pickers, and minimal creation.</summary>
public sealed class CategoryService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer) : ICategoryService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(bool includeHidden, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var rows = await db.Categories.AsNoTracking()
                    .Where(c => includeHidden || (!c.IsHidden && !c.Group!.IsHidden))
                    .Select(c => new
                    {
                        c.Id,
                        c.GroupId,
                        GroupName = c.Group!.Name,
                        GroupSort = c.Group.SortOrder,
                        c.Name,
                        c.SortOrder,
                        c.IsSystem,
                        c.IsHidden,
                        c.LinkedAccountId,
                    })
                    .ToListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<CategoryDto> result = rows
                    .OrderBy(r => r.GroupSort).ThenBy(r => r.GroupName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(r => new CategoryDto(r.Id, r.GroupId, r.GroupName, r.Name, r.IsSystem, r.IsHidden, r.LinkedAccountId))
                    .ToList();
                return result;
            }
        },
        ct);

    /// <inheritdoc />
    public Task<CategoryDto> CreateCategoryAsync(string groupName, string name, CancellationToken ct)
    {
        var group = PayeeNames.Clean(groupName);
        var clean = PayeeNames.Clean(name);
        if (group.Length == 0 || clean.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.CategoryNameRequired);
        }

        return writer.RunAsync(
            LedgerAction.CreateCategory,
            async session =>
            {
                var db = session.Db;
                var groups = await db.CategoryGroups.ToListAsync(ct).ConfigureAwait(false);
                var owner = groups.FirstOrDefault(g => !g.IsSystem && string.Equals(g.Name, group, StringComparison.CurrentCultureIgnoreCase));
                if (owner is null)
                {
                    owner = new CategoryGroup { Name = group, SortOrder = groups.Count == 0 ? 0 : groups.Max(g => g.SortOrder) + 1 };
                    db.CategoryGroups.Add(owner);
                }

                var sort = await db.Categories.Where(c => c.GroupId == owner.Id).Select(c => (int?)c.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
                var category = new Category { GroupId = owner.Id, Name = clean, SortOrder = sort + 1 };
                db.Categories.Add(category);
                return new CategoryDto(category.Id, owner.Id, owner.Name, category.Name, false, false, null);
            },
            ct);
    }
}
