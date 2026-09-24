using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels;

/// <summary>Home dashboard (PRD 9.2). Empty until accounts exist.</summary>
public sealed class HomeViewModel(AppSession session) : PageViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.Page_Home_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Home_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Home_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Home_EmptyMessage;

    /// <summary>Name of the open budget file.</summary>
    public string FileName => session.BudgetFile?.FileName ?? Strings.Shell_NoFile;

    /// <summary>Full path of the open budget file.</summary>
    public string FilePath => session.BudgetFile?.Path ?? string.Empty;

    /// <summary>Label for the budget file line.</summary>
    public string FileLabel => Strings.Page_Home_FileLabel;
}

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

/// <summary>Goals (PRD 9.7).</summary>
public sealed class GoalsViewModel : PageViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.Page_Goals_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Goals_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Goals_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Goals_EmptyMessage;
}

/// <summary>Reports (PRD 9.8).</summary>
public sealed class ReportsViewModel : PageViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.Page_Reports_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Reports_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Reports_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Reports_EmptyMessage;
}
