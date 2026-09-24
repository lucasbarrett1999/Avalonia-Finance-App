using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels;

/// <summary>Monthly budget grid (PRD 9.3).</summary>
public sealed class BudgetViewModel : PageViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.Page_Budget_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Budget_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Budget_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Budget_EmptyMessage;
}

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
