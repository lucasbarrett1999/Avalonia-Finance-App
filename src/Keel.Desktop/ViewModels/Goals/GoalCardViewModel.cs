using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Goals;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Reports;

namespace Keel.Desktop.ViewModels.Goals;

/// <summary>Where a goal stands at the projected pace.</summary>
public enum GoalStatus
{
    /// <summary>The balance reaches the target.</summary>
    Reached,

    /// <summary>Reached by the target date at this pace.</summary>
    OnTrack,

    /// <summary>Reached, but after the target date.</summary>
    Behind,

    /// <summary>Not reached at this pace (nothing assigned lately).</summary>
    NoPace,
}

/// <summary>
/// A goal card (PRD 9.7): progress ring (available / target), monthly need, projected completion
/// at the current pace (average assigned over the last three months), and a "what if I added
/// $X/month" slider that updates the projection live.
/// </summary>
public sealed partial class GoalCardViewModel : ObservableObject
{
    private readonly DateOnly _month;

    /// <summary>Creates the card for a goal as of <paramref name="month"/>.</summary>
    public GoalCardViewModel(GoalDto goal, DateOnly month)
    {
        ArgumentNullException.ThrowIfNull(goal);
        Goal = goal;
        _month = BudgetMonth.Of(month);
        var unit = Currency.MinorUnitsPerMajor(Currency.IsValidCode(goal.Currency) ? goal.Currency : Currency.Default);
        var need = Math.Max(goal.MonthlyNeed, goal.AveragePace);
        var max = Math.Max(100, Math.Ceiling(need * 2.0 / unit / 50) * 50);
        SliderMaximum = max;
        SliderStep = max >= 2_000 ? 50 : 10;
        Recalculate();
    }

    /// <summary>The goal.</summary>
    public GoalDto Goal { get; }

    /// <summary>Category id.</summary>
    public Guid CategoryId => Goal.CategoryId;

    /// <summary>Name.</summary>
    public string Name => Goal.Name;

    /// <summary>Fraction saved (available / target), 0..1.</summary>
    public double Progress => Goal.Target <= 0 ? 0 : Math.Clamp((double)Goal.Available / Goal.Target, 0, 1);

    /// <summary>"33%".</summary>
    public string PercentText => Progress.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>"$400.00 of $1,200.00".</summary>
    public string ProgressText => LedgerText.Format(Strings.Goals_ProgressOf, Money(Goal.Available), Money(Goal.Target));

    /// <summary>"by March 2027".</summary>
    public string TargetDateText => LedgerText.Format(Strings.Goals_By, Goal.TargetDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture));

    /// <summary>"$200.00 a month needed".</summary>
    public string MonthlyNeedText => Goal.Available >= Goal.Target
        ? Strings.Goals_NothingNeeded
        : LedgerText.Format(Strings.Goals_MonthlyNeed, Money(Goal.MonthlyNeed));

    /// <summary>"Current pace: $150.00 a month (average of the last 3 months)".</summary>
    public string PaceText => LedgerText.Format(Strings.Goals_Pace, Money(Goal.AveragePace), GoalProjection.PaceMonths);

    /// <summary>Linked tracking account, e.g. "Kept in Brokerage ($1,000.00)".</summary>
    public string? LinkedAccountText => Goal.LinkedAccountName is { } name
        ? LedgerText.Format(Strings.Goals_LinkedAccount, name, Money(Goal.LinkedAccountBalance ?? 0))
        : null;

    /// <summary>Whether a linked account exists.</summary>
    public bool HasLinkedAccount => Goal.LinkedAccountName is not null;

    /// <summary>Upper end of the what-if slider (major units).</summary>
    public double SliderMaximum { get; }

    /// <summary>Slider step (major units).</summary>
    public double SliderStep { get; }

    /// <summary>Extra per month in whole major units (the what-if slider).</summary>
    [ObservableProperty]
    public partial double ExtraPerMonth { get; set; }

    /// <summary>"+$50.00 a month".</summary>
    [ObservableProperty]
    public partial string ExtraText { get; private set; } = string.Empty;

    /// <summary>The projection at pace plus the extra.</summary>
    [ObservableProperty]
    public partial GoalProjectionResult Projection { get; private set; } = null!;

    /// <summary>Status at the projected pace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnTrack), nameof(IsBehind), nameof(IsReached), nameof(StatusText))]
    public partial GoalStatus Status { get; private set; }

    /// <summary>On track (or reached).</summary>
    public bool IsOnTrack => Status is GoalStatus.OnTrack or GoalStatus.Reached;

    /// <summary>Behind or not moving.</summary>
    public bool IsBehind => Status is GoalStatus.Behind or GoalStatus.NoPace;

    /// <summary>Reached.</summary>
    public bool IsReached => Status == GoalStatus.Reached;

    /// <summary>"On track", "Behind", "Reached", "Not moving" (words beside the icon, never colour alone).</summary>
    public string StatusText => Status switch
    {
        GoalStatus.Reached => Strings.Goals_StatusReached,
        GoalStatus.OnTrack => Strings.Goals_StatusOnTrack,
        GoalStatus.Behind => Strings.Goals_StatusBehind,
        _ => Strings.Goals_StatusNoPace,
    };

    /// <summary>"Reached in November 2026 (4 months), 2 months early".</summary>
    [ObservableProperty]
    public partial string ProjectionText { get; private set; } = string.Empty;

    /// <summary>Monthly amount the projection uses (pace + extra), minor units.</summary>
    public long ProjectedMonthly => Goal.AveragePace + ExtraMinor;

    private long ExtraMinor => (long)Math.Round(ExtraPerMonth, MidpointRounding.ToEven) * Currency.MinorUnitsPerMajor(Currency.IsValidCode(Goal.Currency) ? Goal.Currency : Currency.Default);

    partial void OnExtraPerMonthChanged(double value) => Recalculate();

    private void Recalculate()
    {
        var projection = GoalProjection.Project(Goal.Available, Goal.Target, ProjectedMonthly, _month);
        Projection = projection;
        ExtraText = ExtraMinor == 0 ? Strings.Goals_WhatIfNone : LedgerText.Format(Strings.Goals_WhatIfExtra, Money(ExtraMinor));
        var targetMonth = BudgetMonth.Of(Goal.TargetDate);
        if (projection.IsComplete)
        {
            Status = GoalStatus.Reached;
            ProjectionText = Strings.Goals_ProjectionReached;
        }
        else if (projection.CompletionMonth is not { } done)
        {
            Status = GoalStatus.NoPace;
            ProjectionText = Strings.Goals_ProjectionNoPace;
        }
        else
        {
            var when = done.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            var late = BudgetMonth.Between(targetMonth, done);
            Status = late <= 0 ? GoalStatus.OnTrack : GoalStatus.Behind;
            ProjectionText = late switch
            {
                < 0 => LedgerText.Format(Strings.Goals_ProjectionEarly, when, projection.MonthsToGo, -late),
                0 => LedgerText.Format(Strings.Goals_ProjectionOnTime, when, projection.MonthsToGo),
                _ => LedgerText.Format(Strings.Goals_ProjectionLate, when, projection.MonthsToGo, late),
            };
        }
    }

    private string Money(long amount) => ReportFormat.Money(amount, Goal.Currency);
}
