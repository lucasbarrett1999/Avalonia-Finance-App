using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels;

/// <summary>Bills and subscriptions (PRD 9.6).</summary>
public sealed class BillsViewModel : PageViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.Page_Bills_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Bills_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Bills_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Bills_EmptyMessage;
}
