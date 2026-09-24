using Keel.Application.Ledger;

namespace Keel.Desktop.ViewModels;

/// <summary>The sidebar Review badge (PRD 9.1): the live count of unapproved transactions.</summary>
public sealed partial class ShellViewModel
{
    private static readonly RegisterFilter UnapprovedFilter = new(UnapprovedOnly: true);
    private IRegisterQuery? _reviewQuery;
    private int _badgeVersion;

    /// <summary>The latest badge refresh (tests await it).</summary>
    public Task ReviewBadgeLoading { get; private set; } = Task.CompletedTask;

    /// <summary>The Review entry of the sidebar.</summary>
    public NavigationItemViewModel ReviewItem => PrimaryItems.First(i => i.PageType == typeof(ReviewViewModel));

    /// <summary>Recounts unapproved transactions for the badge.</summary>
    public async Task RefreshReviewBadgeAsync()
    {
        if (_reviewQuery is not { } query)
        {
            return;
        }

        var version = ++_badgeVersion;
        try
        {
            var count = await query.CountAsync(UnapprovedFilter, CancellationToken.None);
            if (version == _badgeVersion)
            {
                ReviewItem.Badge = count;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No budget file open, or it cannot be read: no badge (the shell reports the file problem).
        }
    }

    private void StartReviewBadge(IRegisterQuery register, bool hasFile)
    {
        _reviewQuery = register;
        ReviewBadgeLoading = hasFile ? RefreshReviewBadgeAsync() : Task.CompletedTask;
    }
}
