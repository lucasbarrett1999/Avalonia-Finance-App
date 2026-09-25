using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Stats;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>How a metric compares with its PRD 4 target.</summary>
public enum StatsOutcome
{
    /// <summary>No measurement yet.</summary>
    NotMeasured,

    /// <summary>Meets the target.</summary>
    Met,

    /// <summary>Misses the target.</summary>
    NotMet,
}

/// <summary>One metric on the Stats page: value, target, outcome and how it is computed.</summary>
/// <param name="Id">Stable id (tests, automation).</param>
/// <param name="Title">What is measured.</param>
/// <param name="ValueText">The value, or why there is none.</param>
/// <param name="TargetText">"Target: …".</param>
/// <param name="Outcome">Met, not met, not measured.</param>
/// <param name="Detail">Sample details (when, where from), or null.</param>
/// <param name="Explanation">How it is computed, in plain words.</param>
public sealed record StatsMetricViewModel(string Id, string Title, string ValueText, string TargetText, StatsOutcome Outcome, string? Detail, string Explanation)
{
    /// <summary>Outcome in words (colour is never the only signal).</summary>
    public string OutcomeText => Outcome switch
    {
        StatsOutcome.Met => Strings.Stats_Met,
        StatsOutcome.NotMet => Strings.Stats_NotMet,
        _ => Strings.Stats_NotMeasured,
    };

    /// <summary>Icon key of the outcome.</summary>
    public string OutcomeIcon => Outcome switch
    {
        StatsOutcome.Met => "Icon.CheckCircle",
        StatsOutcome.NotMet => "Icon.Alert",
        _ => "Icon.Circle",
    };

    /// <summary>Met.</summary>
    public bool IsMet => Outcome == StatsOutcome.Met;

    /// <summary>Not met.</summary>
    public bool IsNotMet => Outcome == StatsOutcome.NotMet;

    /// <summary>Not measured.</summary>
    public bool IsNotMeasured => Outcome == StatsOutcome.NotMeasured;

    /// <summary>Whether <see cref="Detail"/> is set.</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>Screen-reader summary of the card.</summary>
    public string AutomationName => LedgerText.Format(Strings.Stats_StatusName, Title, ValueText, TargetText, OutcomeText);
}

/// <summary>
/// Settings → Privacy &amp; Stats (PRD 4, ADR 0102): the five success metrics with their targets and a plain
/// explanation, computed on demand from the open file and settings.json. Nothing is transmitted.
/// </summary>
public sealed partial class StatsSettingsViewModel(IStatsService stats, AppSession session, TimeProvider time) : ViewModelBase
{
    /// <summary>The metrics, in PRD order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetrics))]
    public partial IReadOnlyList<StatsMetricViewModel> Metrics { get; private set; } = [];

    /// <summary>Whether metrics are shown.</summary>
    public bool HasMetrics => Metrics.Count > 0;

    /// <summary>Computing.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the stats could not be computed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => Error is not null;

    /// <summary>"Computed 10:42."</summary>
    [ObservableProperty]
    public partial string? ComputedText { get; private set; }

    /// <summary>No budget file is open.</summary>
    public bool HasNoFile => session.BudgetFile is null;

    /// <summary>The latest computation (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Recomputes the metrics.</summary>
    public Task LoadAsync() => Loading = LoadCoreAsync();

    /// <summary>Recomputes the metrics (button and palette).</summary>
    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    /// <summary>Builds the rows of a report (pure; the page and tests use it).</summary>
    public static IReadOnlyList<StatsMetricViewModel> Build(StatsReport report, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(report);
        return [FirstBudget(report.FirstBudget, now), Review(report.Review), Reimport(report.Reimport), ColdStart(report.ColdStart), Scroll(report.Scroll)];
    }

    /// <summary>"9 min 12 s", "1 h 5 min", "3 days 2 h".</summary>
    public static string Duration(TimeSpan value) =>
        value.TotalMinutes < 1 ? LedgerText.Format("{0} s", Math.Max(0, (int)value.TotalSeconds))
        : value.TotalHours < 1 ? LedgerText.Format("{0} min {1} s", (int)value.TotalMinutes, value.Seconds)
        : value.TotalDays < 1 ? LedgerText.Format("{0} h {1} min", (int)value.TotalHours, value.Minutes)
        : LedgerText.Format("{0} days {1} h", (int)value.TotalDays, value.Hours);

    private static StatsMetricViewModel FirstBudget(FirstBudgetStat stat, DateTime now)
    {
        var target = LedgerText.Format(Strings.Stats_Target, Strings.Stats_FirstBudget_Target);
        var detail = stat.StartedAt is { } started
            ? LedgerText.Format(stat.Start == FirstBudgetStart.FirstLaunch ? Strings.Stats_FirstBudget_FromLaunch : Strings.Stats_FirstBudget_FromFile, When(started))
            : null;
        if (stat.Duration is { } duration)
        {
            return new("first-budget", Strings.Stats_FirstBudget_Title, Duration(duration), target, duration < StatsReport.FirstBudgetTarget ? StatsOutcome.Met : StatsOutcome.NotMet, detail, Strings.Stats_FirstBudget_Explain);
        }

        var value = stat.StartedAt is { } start
            ? LedgerText.Format(Strings.Stats_FirstBudget_Pending, Duration(now > start ? now - start : TimeSpan.Zero))
            : Strings.Stats_FirstBudget_NoStart;
        return new("first-budget", Strings.Stats_FirstBudget_Title, value, target, StatsOutcome.NotMeasured, detail, Strings.Stats_FirstBudget_Explain);
    }

    private static StatsMetricViewModel Review(ReviewAccuracyStat stat)
    {
        var target = LedgerText.Format(Strings.Stats_Target, Strings.Stats_Review_Target);
        if (stat.Rate is not { } rate)
        {
            return new("review", Strings.Stats_Review_Title, Strings.Stats_Review_None, target, StatsOutcome.NotMeasured, null, Strings.Stats_Review_Explain);
        }

        var value = LedgerText.Format(Strings.Stats_Review_Value, Percent(rate), stat.Unchanged.ToString("N0", CultureInfo.CurrentCulture), stat.Approved.ToString("N0", CultureInfo.CurrentCulture));
        return new("review", Strings.Stats_Review_Title, value, target, rate > StatsReport.ReviewTarget ? StatsOutcome.Met : StatsOutcome.NotMet, null, Strings.Stats_Review_Explain);
    }

    private static StatsMetricViewModel Reimport(ReimportStat stat)
    {
        var target = LedgerText.Format(Strings.Stats_Target, Strings.Stats_Reimport_Target);
        if (stat.Rate is not { } rate)
        {
            return new("reimport", Strings.Stats_Reimport_Title, Strings.Stats_Reimport_None, target, StatsOutcome.NotMeasured, null, Strings.Stats_Reimport_Explain);
        }

        var value = LedgerText.Format(
            Strings.Stats_Reimport_Value,
            Percent(rate),
            stat.Flagged.ToString("N0", CultureInfo.CurrentCulture),
            stat.Rows.ToString("N0", CultureInfo.CurrentCulture),
            stat.Reimports.ToString("N0", CultureInfo.CurrentCulture));
        var detail = stat.LastAt is { } last ? LedgerText.Format(Strings.Stats_Measured, When(last)) : null;
        return new("reimport", Strings.Stats_Reimport_Title, value, target, rate >= StatsReport.ReimportTarget ? StatsOutcome.Met : StatsOutcome.NotMet, detail, Strings.Stats_Reimport_Explain);
    }

    private static StatsMetricViewModel ColdStart(ColdStartSample? sample)
    {
        var target = LedgerText.Format(Strings.Stats_Target, Strings.Stats_ColdStart_Target);
        if (sample is null)
        {
            return new("cold-start", Strings.Stats_ColdStart_Title, Strings.Stats_ColdStart_None, target, StatsOutcome.NotMeasured, null, Strings.Stats_ColdStart_Explain);
        }

        var seconds = (sample.Milliseconds / 1000.0).ToString("0.0 's'", CultureInfo.CurrentCulture);
        var value = LedgerText.Format(sample.Encrypted ? Strings.Stats_ColdStart_ValueEncrypted : Strings.Stats_ColdStart_Value, seconds, sample.Transactions.ToString("N0", CultureInfo.CurrentCulture));
        var outcome = sample.Milliseconds < StatsReport.ColdStartTarget.TotalMilliseconds ? StatsOutcome.Met : StatsOutcome.NotMet;
        return new("cold-start", Strings.Stats_ColdStart_Title, value, target, outcome, LedgerText.Format(Strings.Stats_Measured, When(sample.MeasuredAt)), Strings.Stats_ColdStart_Explain);
    }

    private static StatsMetricViewModel Scroll(RegisterScrollSample? sample)
    {
        var target = LedgerText.Format(Strings.Stats_Target, Strings.Stats_Scroll_Target);
        if (sample is null)
        {
            return new("scroll", Strings.Stats_Scroll_Title, Strings.Stats_Scroll_None, target, StatsOutcome.NotMeasured, null, Strings.Stats_Scroll_Explain);
        }

        var value = LedgerText.Format(
            Strings.Stats_Scroll_Value,
            sample.AverageMilliseconds.ToString("0.0", CultureInfo.CurrentCulture),
            sample.P95Milliseconds.ToString("0.0", CultureInfo.CurrentCulture),
            sample.Frames.ToString("N0", CultureInfo.CurrentCulture),
            sample.Rows.ToString("N0", CultureInfo.CurrentCulture));
        var outcome = sample.P95Milliseconds <= StatsReport.FrameTargetMilliseconds + 0.05 ? StatsOutcome.Met : StatsOutcome.NotMet;
        return new("scroll", Strings.Stats_Scroll_Title, value, target, outcome, LedgerText.Format(Strings.Stats_Measured, When(sample.MeasuredAt)), Strings.Stats_Scroll_Explain);
    }

    private static string Percent(double rate) => rate.ToString("0.#%", CultureInfo.CurrentCulture);

    private static string When(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private async Task LoadCoreAsync()
    {
        if (session.BudgetFile is null)
        {
            Metrics = [];
            return;
        }

        IsLoading = true;
        Error = null;
        try
        {
            var report = await Task.Run(() => stats.GetAsync(CancellationToken.None));
            Metrics = Build(report, time.GetUtcNow().UtcDateTime);
            ComputedText = LedgerText.Format(Strings.Stats_ComputedAt, report.ComputedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Error = LedgerText.Format(Strings.Stats_Error, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
