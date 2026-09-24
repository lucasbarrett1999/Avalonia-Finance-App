using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>
/// Category lookup for pickers and category management (F-BUD-1, F-BUD-7, F-BUD-8). Every change
/// runs through <see cref="LedgerWriter"/>, so it is audited, undoable and announced with
/// <see cref="Application.Messaging.LedgerChanged"/>. System groups and categories are protected.
/// </summary>
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

    /// <inheritdoc />
    public Task<IReadOnlyList<CategoryGroupDto>> GetGroupsAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.Id != SystemIds.InflowGroup).ToListAsync(ct).ConfigureAwait(false);
                var categories = await db.Categories.AsNoTracking().Where(c => c.GroupId != SystemIds.InflowGroup).ToListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<CategoryGroupDto> result = groups
                    .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new CategoryGroupDto(
                        g.Id,
                        g.Name,
                        g.IsSystem,
                        g.IsHidden,
                        categories.Where(c => c.GroupId == g.Id)
                            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                            .Select(c => new CategoryDto(c.Id, g.Id, g.Name, c.Name, c.IsSystem, c.IsHidden, c.LinkedAccountId))
                            .ToList()))
                    .ToList();
                return result;
            }
        },
        ct);

    /// <inheritdoc />
    public Task<CategoryGroupDto> CreateGroupAsync(string name, CancellationToken ct)
    {
        var clean = RequireName(name);
        return writer.RunAsync(
            LedgerAction.CreateCategoryGroup,
            async session =>
            {
                var max = await session.Db.CategoryGroups.Select(g => (int?)g.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                var group = new CategoryGroup { Name = clean, SortOrder = max + 1 };
                session.Db.CategoryGroups.Add(group);
                return new CategoryGroupDto(group.Id, group.Name, false, false, []);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<CategoryDto> CreateCategoryAsync(Guid groupId, string name, CancellationToken ct)
    {
        var clean = RequireName(name);
        return writer.RunAsync(
            LedgerAction.CreateCategory,
            async session =>
            {
                var db = session.Db;
                var group = await UserGroupAsync(db, groupId, ct).ConfigureAwait(false);
                var sort = await db.Categories.Where(c => c.GroupId == group.Id).Select(c => (int?)c.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
                var category = new Category { GroupId = group.Id, Name = clean, SortOrder = sort + 1 };
                db.Categories.Add(category);
                return new CategoryDto(category.Id, group.Id, group.Name, category.Name, false, false, null);
            },
            ct);
    }

    /// <inheritdoc />
    public Task RenameGroupAsync(Guid groupId, string name, CancellationToken ct)
    {
        var clean = RequireName(name);
        return writer.RunAsync(
            LedgerAction.RenameCategoryGroup,
            async session =>
            {
                (await UserGroupAsync(session.Db, groupId, ct).ConfigureAwait(false)).Name = clean;
                return true;
            },
            ct);
    }

    /// <inheritdoc />
    public Task SetGroupHiddenAsync(Guid groupId, bool hidden, CancellationToken ct) => writer.RunAsync(
        LedgerAction.HideCategoryGroup,
        async session =>
        {
            (await UserGroupAsync(session.Db, groupId, ct).ConfigureAwait(false)).IsHidden = hidden;
            return true;
        },
        ct);

    /// <inheritdoc />
    public Task ReorderGroupsAsync(IReadOnlyList<Guid> order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        return writer.RunAsync(
            LedgerAction.ReorderCategoryGroups,
            async session =>
            {
                var groups = await session.Db.CategoryGroups.Where(g => g.Id != SystemIds.InflowGroup).ToListAsync(ct).ConfigureAwait(false);
                if (order.Count != groups.Count || order.Distinct().Count() != order.Count || order.Any(id => groups.All(g => g.Id != id)))
                {
                    throw new ArgumentException("The order must list every category group exactly once.", nameof(order));
                }

                for (var i = 0; i < order.Count; i++)
                {
                    groups.Single(g => g.Id == order[i]).SortOrder = i + 1;   // Inflow keeps 0
                }

                return true;
            },
            ct);
    }

    /// <inheritdoc />
    public Task DeleteGroupAsync(Guid groupId, Guid? replacementCategoryId, CancellationToken ct) => writer.RunAsync(
        LedgerAction.DeleteCategoryGroup,
        async session =>
        {
            var db = session.Db;
            var group = await UserGroupAsync(db, groupId, ct).ConfigureAwait(false);
            var categories = await db.Categories.Where(c => c.GroupId == group.Id).ToListAsync(ct).ConfigureAwait(false);
            var ids = categories.Select(c => c.Id).ToHashSet();
            foreach (var category in categories)
            {
                await DeleteCategoryCoreAsync(db, category, replacementCategoryId, ids, ct).ConfigureAwait(false);
            }

            db.CategoryGroups.Remove(group);
            return true;
        },
        ct);

    /// <inheritdoc />
    public Task RenameCategoryAsync(Guid categoryId, string name, CancellationToken ct)
    {
        var clean = RequireName(name);
        return writer.RunAsync(
            LedgerAction.RenameCategory,
            async session =>
            {
                (await UserCategoryAsync(session.Db, categoryId, ct).ConfigureAwait(false)).Name = clean;
                return true;
            },
            ct);
    }

    /// <inheritdoc />
    public Task SetCategoryHiddenAsync(Guid categoryId, bool hidden, CancellationToken ct) => writer.RunAsync(
        LedgerAction.HideCategory,
        async session =>
        {
            (await UserCategoryAsync(session.Db, categoryId, ct).ConfigureAwait(false)).IsHidden = hidden;
            return true;
        },
        ct);

    /// <inheritdoc />
    public Task MoveCategoryAsync(Guid categoryId, Guid groupId, int index, CancellationToken ct) => writer.RunAsync(
        LedgerAction.MoveCategory,
        async session =>
        {
            var db = session.Db;
            var category = await UserCategoryAsync(db, categoryId, ct).ConfigureAwait(false);
            var target = await UserGroupAsync(db, groupId, ct).ConfigureAwait(false);
            var source = category.GroupId;
            var siblings = await db.Categories.Where(c => c.GroupId == target.Id && c.Id != category.Id).ToListAsync(ct).ConfigureAwait(false);
            var order = siblings.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            order.Insert(Math.Clamp(index, 0, order.Count), category);
            category.GroupId = target.Id;
            Renumber(order);

            if (source != target.Id)
            {
                var left = await db.Categories.Where(c => c.GroupId == source && c.Id != category.Id).ToListAsync(ct).ConfigureAwait(false);
                Renumber(left.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            }

            return true;
        },
        ct);

    /// <inheritdoc />
    public Task<CategoryUsageDto> GetUsageAsync(Guid categoryId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await UsageAsync(db, categoryId, ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public Task DeleteCategoryAsync(Guid categoryId, Guid? replacementCategoryId, CancellationToken ct) => writer.RunAsync(
        LedgerAction.DeleteCategory,
        async session =>
        {
            var category = await UserCategoryAsync(session.Db, categoryId, ct).ConfigureAwait(false);
            await DeleteCategoryCoreAsync(session.Db, category, replacementCategoryId, new HashSet<Guid> { category.Id }, ct).ConfigureAwait(false);
            return true;
        },
        ct);

    /// <inheritdoc />
    public Task<string?> GetNoteAsync(Guid categoryId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var row = await db.Categories.AsNoTracking().Where(c => c.Id == categoryId).Select(c => new { c.Notes }).SingleOrDefaultAsync(ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.CategoryNotFound);
                return row.Notes;
            }
        },
        ct);

    /// <inheritdoc />
    public Task SetNoteAsync(Guid categoryId, string? note, CancellationToken ct)
    {
        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return writer.RunAsync(
            LedgerAction.EditCategoryNote,
            async session =>
            {
                var category = await session.Db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.CategoryNotFound);
                category.Notes = clean;
                return true;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<int> ApplyTemplateAsync(IReadOnlyList<CategoryTemplateGroup> groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return writer.RunAsync(
            LedgerAction.ApplyCategoryTemplate,
            async session =>
            {
                var db = session.Db;
                var existingGroups = await db.CategoryGroups.ToListAsync(ct).ConfigureAwait(false);
                var existingCategories = await db.Categories.ToListAsync(ct).ConfigureAwait(false);
                var nextGroupSort = existingGroups.Count == 0 ? 1 : existingGroups.Max(g => g.SortOrder) + 1;
                var created = 0;
                foreach (var template in groups)
                {
                    var groupName = PayeeNames.Clean(template.Name);
                    if (groupName.Length == 0)
                    {
                        continue;
                    }

                    var group = existingGroups.FirstOrDefault(g => !g.IsSystem && string.Equals(g.Name, groupName, StringComparison.CurrentCultureIgnoreCase));
                    if (group is null)
                    {
                        group = new CategoryGroup { Name = groupName, SortOrder = nextGroupSort++ };
                        db.CategoryGroups.Add(group);
                        existingGroups.Add(group);
                    }

                    var sort = existingCategories.Where(c => c.GroupId == group.Id).Select(c => (int?)c.SortOrder).Max() ?? -1;
                    foreach (var name in template.Categories.Select(PayeeNames.Clean).Where(n => n.Length > 0))
                    {
                        if (existingCategories.Any(c => c.GroupId == group.Id && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                        {
                            continue;
                        }

                        var category = new Category { GroupId = group.Id, Name = name, SortOrder = ++sort };
                        db.Categories.Add(category);
                        existingCategories.Add(category);
                        created++;
                    }
                }

                return created;
            },
            ct);
    }

    private static string RequireName(string? name)
    {
        var clean = PayeeNames.Clean(name);
        return clean.Length == 0 ? throw new LedgerValidationException(LedgerError.CategoryNameRequired) : clean;
    }

    private static void Renumber(List<Category> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SortOrder != i)
            {
                ordered[i].SortOrder = i;
            }
        }
    }

    private static async Task<CategoryGroup> UserGroupAsync(KeelDbContext db, Guid groupId, CancellationToken ct)
    {
        var group = await db.CategoryGroups.SingleOrDefaultAsync(g => g.Id == groupId, ct).ConfigureAwait(false)
            ?? throw new LedgerValidationException(LedgerError.CategoryGroupNotFound);
        return group.IsSystem ? throw new LedgerValidationException(LedgerError.SystemCategoryProtected) : group;
    }

    private static async Task<Category> UserCategoryAsync(KeelDbContext db, Guid categoryId, CancellationToken ct)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, ct).ConfigureAwait(false)
            ?? throw new LedgerValidationException(LedgerError.CategoryNotFound);
        return IsProtected(category) ? throw new LedgerValidationException(LedgerError.SystemCategoryProtected) : category;
    }

    private static bool IsProtected(Category category) =>
        category.IsSystem || category.LinkedAccountId is not null || category.Id == SystemIds.ReadyToAssignCategory
        || category.GroupId == SystemIds.InflowGroup || category.GroupId == SystemIds.CreditCardPaymentsGroup;

    private static async Task<CategoryUsageDto> UsageAsync(KeelDbContext db, Guid categoryId, CancellationToken ct)
    {
        var transactions = await db.Transactions.IgnoreQueryFilters().CountAsync(t => t.CategoryId == categoryId, ct).ConfigureAwait(false)
            + await db.TransactionSplits.IgnoreQueryFilters().CountAsync(s => s.CategoryId == categoryId, ct).ConfigureAwait(false);
        var assignments = await db.BudgetAssignments.CountAsync(a => a.CategoryId == categoryId, ct).ConfigureAwait(false);
        var scheduled = await db.ScheduledTransactions.CountAsync(s => s.CategoryId == categoryId, ct).ConfigureAwait(false);
        var hasTarget = await db.Targets.AnyAsync(t => t.CategoryId == categoryId, ct).ConfigureAwait(false);
        return new CategoryUsageDto(transactions, assignments, scheduled, hasTarget);
    }

    // Moves everything that refers to the category onto the replacement, then removes it. Every
    // change is a tracked entity, so the unit of work audits it and undo can replay it.
    private static async Task DeleteCategoryCoreAsync(KeelDbContext db, Category category, Guid? replacementId, HashSet<Guid> deleting, CancellationToken ct)
    {
        var usage = await UsageAsync(db, category.Id, ct).ConfigureAwait(false);
        Guid? replacement = null;
        if (usage.HasHistory)
        {
            if (replacementId is not { } id)
            {
                throw new LedgerValidationException(LedgerError.ReplacementCategoryRequired);
            }

            var target = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
            if (target is null || deleting.Contains(id) || IsProtected(target))
            {
                throw new LedgerValidationException(LedgerError.InvalidReplacementCategory);
            }

            replacement = id;
        }

        var deletedId = category.Id;
        foreach (var txn in await db.Transactions.IgnoreQueryFilters().Where(t => t.CategoryId == deletedId).ToListAsync(ct).ConfigureAwait(false))
        {
            txn.CategoryId = replacement;
        }

        foreach (var split in await db.TransactionSplits.IgnoreQueryFilters().Where(s => s.CategoryId == deletedId).ToListAsync(ct).ConfigureAwait(false))
        {
            split.CategoryId = replacement;
        }

        foreach (var scheduled in await db.ScheduledTransactions.Where(s => s.CategoryId == deletedId).ToListAsync(ct).ConfigureAwait(false))
        {
            scheduled.CategoryId = replacement;
        }

        foreach (var item in await db.RecurringItems.Where(r => r.CategoryId == deletedId).ToListAsync(ct).ConfigureAwait(false))
        {
            item.CategoryId = replacement;
        }

        foreach (var payee in await db.Payees.Where(p => p.DefaultCategoryId == deletedId).ToListAsync(ct).ConfigureAwait(false))
        {
            payee.DefaultCategoryId = replacement;
        }

        // Assignments move to the replacement month by month, so Ready to Assign does not change.
        foreach (var assignment in await db.BudgetAssignments.Where(a => a.CategoryId == deletedId).ToListAsync(ct).ConfigureAwait(false))
        {
            db.BudgetAssignments.Remove(assignment);
            if (replacement is { } to)
            {
                await AddAssignmentAsync(db, to, assignment.Month, assignment.Assigned, ct).ConfigureAwait(false);
            }
        }

        if (await db.Targets.SingleOrDefaultAsync(t => t.CategoryId == deletedId, ct).ConfigureAwait(false) is { } targetRow)
        {
            db.Targets.Remove(targetRow);
        }

        db.Categories.Remove(category);
    }

    private static async Task AddAssignmentAsync(KeelDbContext db, Guid categoryId, DateOnly month, long amount, CancellationToken ct)
    {
        var row = db.BudgetAssignments.Local.FirstOrDefault(a => a.CategoryId == categoryId && a.Month == month)
            ?? await db.BudgetAssignments.SingleOrDefaultAsync(a => a.CategoryId == categoryId && a.Month == month, ct).ConfigureAwait(false);
        if (row is null)
        {
            db.BudgetAssignments.Add(new BudgetAssignment { CategoryId = categoryId, Month = month, Assigned = amount });
            return;
        }

        var entry = db.Entry(row);
        var current = entry.State == EntityState.Deleted ? 0 : row.Assigned;
        var next = checked(current + amount);
        if (next == 0)
        {
            if (entry.State != EntityState.Deleted)
            {
                db.BudgetAssignments.Remove(row);                    // absent row means 0
            }

            return;
        }

        if (entry.State == EntityState.Deleted)
        {
            entry.State = EntityState.Unchanged;
        }

        row.Assigned = next;
    }
}
