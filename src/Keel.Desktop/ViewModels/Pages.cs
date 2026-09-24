using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels;

/// <summary>Review queue (PRD 9.5).</summary>
public sealed class ReviewViewModel : PageViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.Page_Review_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Review_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Review_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Review_EmptyMessage;
}
