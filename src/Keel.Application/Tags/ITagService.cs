namespace Keel.Application.Tags;

/// <summary>Read access to transaction tags (Settings → Bills uses them to mark subscriptions).</summary>
public interface ITagService
{
    /// <summary>All tags by name.</summary>
    Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct);
}

/// <summary>A tag.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name.</param>
public sealed record TagDto(Guid Id, string Name);
