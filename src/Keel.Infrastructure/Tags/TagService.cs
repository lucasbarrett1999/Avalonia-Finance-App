using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Rules;
using Keel.Application.Tags;
using Keel.Application.Undo;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Domain.Rules;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Keel.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tags;

/// <summary>
/// Tags (F-TXN-8, ADR 0096): lists, management (rename, merge, delete) as undoable ledger actions, and the
/// helpers every tag write goes through (<see cref="GetOrAddAsync"/>, <see cref="SetTransactionTagsAsync"/>)
/// so names compare case-insensitively everywhere (the editor, rules and imports).
/// </summary>
public sealed class TagService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer, IMessageBus bus) : ITagService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var tags = await db.Tags.AsNoTracking().Select(t => new TagDto(t.Id, t.Name)).ToListAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<TagDto>)tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        },
        ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TagUsage>> ListAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var tags = await db.Tags.AsNoTracking()
                    .Select(t => new { t.Id, t.Name, Count = db.TransactionTags.Count(tt => tt.TagId == t.Id) })
                    .ToListAsync(ct).ConfigureAwait(false);
                var rules = await RuleRewriter.ReadAllAsync(db, ct).ConfigureAwait(false);
                var subscriptionTags = await DataFileSettings.GetAsync<List<Guid>>(db, DataFileSettings.SubscriptionTags, [], ct).ConfigureAwait(false);
                IReadOnlyList<TagUsage> result = tags
                    .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => new TagUsage(
                        t.Id,
                        t.Name,
                        t.Count,
                        rules.Count(r => NamesTag(r, t.Name)),
                        TagNames.IsReserved(t.Name),
                        subscriptionTags.Contains(t.Id)))
                    .ToList();
                return result;
            }
        },
        ct);

    /// <inheritdoc />
    public async Task<TagDto> RenameAsync(Guid tagId, string name, CancellationToken ct)
    {
        var clean = TagNames.Clean(name);
        if (clean.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.TagNameRequired);
        }

        var (tag, rules) = await writer.RunAsync(
            LedgerAction.RenameTag,
            async session =>
            {
                var db = session.Db;
                var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.TagNotFound);
                if (TagNames.IsReserved(tag.Name) || TagNames.IsReserved(clean))
                {
                    throw new LedgerValidationException(LedgerError.TagReserved);
                }

                var others = await db.Tags.AsNoTracking().Where(t => t.Id != tagId).Select(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
                if (others.Any(o => TagNames.Same(o, clean)))
                {
                    throw new LedgerValidationException(LedgerError.TagNameTaken);
                }

                var old = tag.Name;
                tag.Name = clean;
                var rules = TagNames.Same(old, clean) && string.Equals(old, clean, StringComparison.Ordinal)
                    ? 0
                    : await RenameInRulesAsync(db, old, clean, ct).ConfigureAwait(false);
                return (new TagDto(tag.Id, tag.Name), rules);
            },
            ct).ConfigureAwait(false);
        if (rules > 0)
        {
            bus.Publish(new RulesChanged());
        }

        return tag;
    }

    /// <inheritdoc />
    public async Task<TagMergeResult> MergeAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        if (sourceId == targetId)
        {
            throw new LedgerValidationException(LedgerError.TagMergeSame);
        }

        var result = await writer.RunAsync(
            LedgerAction.MergeTags,
            async session =>
            {
                var db = session.Db;
                var source = await db.Tags.SingleOrDefaultAsync(t => t.Id == sourceId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.TagNotFound);
                var target = await db.Tags.SingleOrDefaultAsync(t => t.Id == targetId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.TagNotFound);
                if (TagNames.IsReserved(source.Name) || TagNames.IsReserved(target.Name))
                {
                    throw new LedgerValidationException(LedgerError.TagReserved);
                }

                // Links are removed and added as tracked rows (deleted transactions' too), so undo restores them.
                var sourceLinks = await db.TransactionTags.IgnoreQueryFilters().Where(t => t.TagId == sourceId).ToListAsync(ct).ConfigureAwait(false);
                var targetTransactions = (await db.TransactionTags.IgnoreQueryFilters().Where(t => t.TagId == targetId).Select(t => t.TransactionId)
                    .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
                var moved = 0;
                foreach (var link in sourceLinks)
                {
                    db.TransactionTags.Remove(link);
                    if (targetTransactions.Add(link.TransactionId))
                    {
                        db.TransactionTags.Add(new TransactionTag { TransactionId = link.TransactionId, TagId = targetId });
                        moved++;
                    }
                }

                var designated = await DataFileSettings.GetAsync<List<Guid>>(db, DataFileSettings.SubscriptionTags, [], ct).ConfigureAwait(false);
                if (designated.Contains(sourceId))
                {
                    var updated = designated.Select(id => id == sourceId ? targetId : id).Distinct().ToList();
                    await DataFileSettings.StageAsync(db, DataFileSettings.SubscriptionTags, updated, ct).ConfigureAwait(false);
                }

                var rules = await RenameInRulesAsync(db, source.Name, target.Name, ct).ConfigureAwait(false);
                db.Tags.Remove(source);
                return new TagMergeResult(new TagDto(target.Id, target.Name), moved, rules);
            },
            ct).ConfigureAwait(false);
        if (result.RulesUpdated > 0)
        {
            bus.Publish(new RulesChanged());
        }

        return result;
    }

    /// <inheritdoc />
    public Task<int> DeleteAsync(Guid tagId, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.DeleteTag,
            async session =>
            {
                var db = session.Db;
                var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.TagNotFound);
                var count = await db.TransactionTags.CountAsync(t => t.TagId == tagId, ct).ConfigureAwait(false);

                // Explicit removals (not the database cascade), so the audit log and undo see every link.
                db.TransactionTags.RemoveRange(await db.TransactionTags.IgnoreQueryFilters().Where(t => t.TagId == tagId).ToListAsync(ct).ConfigureAwait(false));
                var designated = await DataFileSettings.GetAsync<List<Guid>>(db, DataFileSettings.SubscriptionTags, [], ct).ConfigureAwait(false);
                if (designated.Contains(tagId))
                {
                    await DataFileSettings.StageAsync(db, DataFileSettings.SubscriptionTags, designated.Where(id => id != tagId).ToList(), ct).ConfigureAwait(false);
                }

                db.Tags.Remove(tag);
                return count;
            },
            ct);

    /// <summary>
    /// The tracked tag named <paramref name="name"/> (case-insensitive), added to <paramref name="db"/> when it
    /// does not exist yet. Throws <see cref="LedgerValidationException"/> for an empty name.
    /// </summary>
    internal static async Task<Tag> GetOrAddAsync(KeelDbContext db, string name, CancellationToken ct)
    {
        var clean = TagNames.Clean(name);
        if (clean.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.TagNameRequired);
        }

        var tag = db.Tags.Local.FirstOrDefault(t => TagNames.Same(t.Name, clean));
        if (tag is not null)
        {
            return tag;
        }

        // Tags are few; loading them compares names exactly as TagNames does (SQLite's UPPER is ASCII-only).
        var all = await db.Tags.ToListAsync(ct).ConfigureAwait(false);
        tag = all.FirstOrDefault(t => TagNames.Same(t.Name, clean));
        if (tag is null)
        {
            tag = new Tag { Name = clean };
            db.Tags.Add(tag);
        }

        return tag;
    }

    /// <summary>Adds the tag named <paramref name="name"/> to a transaction unless it already has it.</summary>
    internal static async Task AddToTransactionAsync(KeelDbContext db, Guid transactionId, string name, CancellationToken ct)
    {
        var tag = await GetOrAddAsync(db, name, ct).ConfigureAwait(false);
        var tagId = tag.Id;
        var exists = db.TransactionTags.Local.Any(t => t.TransactionId == transactionId && t.TagId == tagId)
            || await db.TransactionTags.IgnoreQueryFilters().AnyAsync(t => t.TransactionId == transactionId && t.TagId == tagId, ct).ConfigureAwait(false);
        if (!exists)
        {
            db.TransactionTags.Add(new TransactionTag { TransactionId = transactionId, TagId = tagId });
        }
    }

    /// <summary>Makes a transaction's tags exactly <paramref name="names"/>, creating new tags as needed (inside the caller's ledger action).</summary>
    internal static async Task SetTransactionTagsAsync(KeelDbContext db, Guid transactionId, IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = new List<Guid>();
        foreach (var name in TagNames.Distinct(names))
        {
            wanted.Add((await GetOrAddAsync(db, name, ct).ConfigureAwait(false)).Id);
        }

        var current = await db.TransactionTags.IgnoreQueryFilters().Where(t => t.TransactionId == transactionId).ToListAsync(ct).ConfigureAwait(false);
        db.TransactionTags.RemoveRange(current.Where(t => !wanted.Contains(t.TagId)));
        foreach (var tagId in wanted.Where(id => current.All(t => t.TagId != id)))
        {
            db.TransactionTags.Add(new TransactionTag { TransactionId = transactionId, TagId = tagId });
        }
    }

    /// <summary>Tag names of each transaction, sorted by name.</summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<string>>> NamesByTransactionAsync(KeelDbContext db, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        var rows = await (from tt in db.TransactionTags.IgnoreQueryFilters().AsNoTracking()
                          join g in db.Tags.AsNoTracking() on tt.TagId equals g.Id
                          where transactionIds.Contains(tt.TransactionId)
                          select new { tt.TransactionId, g.Name }).ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.TransactionId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)g.Select(r => r.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private static bool NamesTag(RuleDefinition rule, string name) =>
        rule.Conditions.Conditions.Any(c => c is TagCondition t && TagNames.Same(t.Tag, name))
        || rule.Actions.Actions.Any(a => a is AddTagAction t && TagNames.Same(t.Tag, name));

    private static Task<int> RenameInRulesAsync(KeelDbContext db, string from, string to, CancellationToken ct) =>
        RuleRewriter.RewriteAsync(
            db,
            c => c is TagCondition t && TagNames.Same(t.Tag, from) ? t with { Tag = to } : c,
            a => a is AddTagAction t && TagNames.Same(t.Tag, from) ? t with { Tag = to } : a,
            ct);
}
