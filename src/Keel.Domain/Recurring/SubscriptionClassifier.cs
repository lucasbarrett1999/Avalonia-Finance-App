using Keel.Domain.Entities;

namespace Keel.Domain.Recurring;

/// <summary>
/// Which categories and tags mark a recurring item as a subscription (F-REC-2): categories in a
/// user-designated subscription group, or a designated tag. The designations are data-file settings
/// that the service loads; this type only holds them.
/// </summary>
/// <param name="SubscriptionCategoryIds">Categories whose group is designated a subscription group.</param>
/// <param name="SubscriptionTagIds">Tags designated as subscription tags.</param>
public sealed record SubscriptionHints(IReadOnlySet<Guid> SubscriptionCategoryIds, IReadOnlySet<Guid> SubscriptionTagIds)
{
    /// <summary>No designations: nothing is a subscription.</summary>
    public static SubscriptionHints None { get; } = new(new HashSet<Guid>(), new HashSet<Guid>());

    /// <summary>Builds hints from the categories of the designated groups.</summary>
    public static SubscriptionHints Build(IEnumerable<Category> categories, IEnumerable<Guid> subscriptionGroupIds, IEnumerable<Guid> subscriptionTagIds)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(subscriptionGroupIds);
        ArgumentNullException.ThrowIfNull(subscriptionTagIds);
        var groups = subscriptionGroupIds.ToHashSet();
        return new SubscriptionHints(
            categories.Where(c => groups.Contains(c.GroupId)).Select(c => c.Id).ToHashSet(),
            subscriptionTagIds.ToHashSet());
    }
}

/// <summary>Subscription vs bill (F-REC-2).</summary>
public static class SubscriptionClassifier
{
    /// <summary>
    /// True when the item's category is in a subscription group or any of the tags on the item's
    /// transactions is a subscription tag.
    /// </summary>
    public static bool IsSubscription(Guid? categoryId, IEnumerable<Guid> tagIds, SubscriptionHints hints)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        ArgumentNullException.ThrowIfNull(hints);
        return (categoryId is { } c && hints.SubscriptionCategoryIds.Contains(c))
            || tagIds.Any(hints.SubscriptionTagIds.Contains);
    }
}
