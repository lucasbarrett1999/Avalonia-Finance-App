using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Import.Migration;

/// <summary>
/// The budget file's category groups and categories for a migration: finds a user category by group and
/// name (ignoring case, hidden ones included) and, when tracking, adds missing groups and categories at the
/// end of the list, as the category service would.
/// </summary>
internal sealed class CategoryCatalog
{
    private readonly KeelDbContext? _db;
    private readonly List<CategoryGroup> _groups;
    private readonly List<Category> _categories;

    private CategoryCatalog(KeelDbContext? db, List<CategoryGroup> groups, List<Category> categories)
    {
        _db = db;
        _groups = groups;
        _categories = categories;
    }

    /// <summary>Groups added.</summary>
    public int GroupsCreated { get; private set; }

    /// <summary>Categories added.</summary>
    public int CategoriesCreated { get; private set; }

    /// <summary>Loads the catalog; <paramref name="track"/> allows <see cref="FindOrAdd"/> to add rows to <paramref name="db"/>.</summary>
    public static async Task<CategoryCatalog> LoadAsync(KeelDbContext db, bool track, CancellationToken ct)
    {
        var groups = track ? await db.CategoryGroups.ToListAsync(ct).ConfigureAwait(false) : await db.CategoryGroups.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var categories = track ? await db.Categories.ToListAsync(ct).ConfigureAwait(false) : await db.Categories.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return new CategoryCatalog(track ? db : null, groups, categories);
    }

    /// <summary>The user category <paramref name="name"/> in the user group <paramref name="group"/>, or null.</summary>
    public Category? Find(string group, string name)
    {
        var owner = Group(PayeeNames.Clean(group));
        var clean = PayeeNames.Clean(name);
        return owner is null ? null : _categories.FirstOrDefault(c => c.GroupId == owner.Id && string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The system Credit Card Payment category named <paramref name="card"/> (named after its account), or null.</summary>
    public Category? FindCreditCardPayment(string card) =>
        _categories.FirstOrDefault(c => c.GroupId == SystemIds.CreditCardPaymentsGroup && string.Equals(c.Name, PayeeNames.Clean(card), StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the category or adds it (and its group).</summary>
    public Category FindOrAdd(string group, string name)
    {
        if (Find(group, name) is { } found)
        {
            return found;
        }

        var db = _db ?? throw new InvalidOperationException("The catalog was loaded read-only.");
        var groupName = Truncate(PayeeNames.Clean(group));
        var owner = Group(groupName);
        if (owner is null)
        {
            owner = new CategoryGroup { Name = groupName, SortOrder = _groups.Count == 0 ? 1 : _groups.Max(g => g.SortOrder) + 1 };
            db.CategoryGroups.Add(owner);
            _groups.Add(owner);
            GroupsCreated++;
        }

        var sort = _categories.Where(c => c.GroupId == owner.Id).Select(c => (int?)c.SortOrder).Max() ?? -1;
        var category = new Category { GroupId = owner.Id, Name = Truncate(PayeeNames.Clean(name)), SortOrder = sort + 1 };
        db.Categories.Add(category);
        _categories.Add(category);
        CategoriesCreated++;
        return category;
    }

    private CategoryGroup? Group(string name) =>
        _groups.FirstOrDefault(g => !g.IsSystem && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string name) => name.Length > 100 ? name[..100] : name;
}
