using Keel.Application.Tags;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tags;

/// <summary>Reads tags from the budget file.</summary>
public sealed class TagService(IDbContextFactory<KeelDbContext> factory) : ITagService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var tags = await db.Tags.AsNoTracking().Select(t => new TagDto(t.Id, t.Name)).ToListAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<TagDto>)tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        },
        ct);
}
