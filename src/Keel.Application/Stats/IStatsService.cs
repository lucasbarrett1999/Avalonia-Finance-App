namespace Keel.Application.Stats;

/// <summary>
/// The private success metrics of PRD section 4, computed on this computer from the open file's audit log and
/// Setting table and from settings.json (Settings → Privacy &amp; Stats). Nothing is ever transmitted.
/// </summary>
public interface IStatsService
{
    /// <summary>Computes every metric now (read-only).</summary>
    Task<StatsReport> GetAsync(CancellationToken ct);
}

/// <summary>The five PRD 4 metrics.</summary>
/// <param name="FirstBudget">Time from first launch to the first assigned budget.</param>
/// <param name="Review">Review approvals accepted without a change over the last 30 days.</param>
/// <param name="Reimport">Rows flagged duplicate when a file is imported again.</param>
/// <param name="ColdStart">The last cold start to interactive.</param>
/// <param name="Scroll">Register scroll frame times from the last 100k-row session.</param>
/// <param name="ComputedAt">When the report was computed (UTC).</param>
public sealed record StatsReport(
    FirstBudgetStat FirstBudget,
    ReviewAccuracyStat Review,
    ReimportStat Reimport,
    ColdStartSample? ColdStart,
    RegisterScrollSample? Scroll,
    DateTime ComputedAt)
{
    /// <summary>PRD 4 target: first assigned budget within 15 minutes of the first launch.</summary>
    public static readonly TimeSpan FirstBudgetTarget = TimeSpan.FromMinutes(15);

    /// <summary>PRD 4 target: more than 85% of review approvals without a change.</summary>
    public const double ReviewTarget = 0.85;

    /// <summary>PRD 4 target: every row of a re-imported file flagged as a duplicate.</summary>
    public const double ReimportTarget = 1.0;

    /// <summary>PRD 4 / PRD 11 target: interactive within 2 s of a cold start (100k transactions).</summary>
    public static readonly TimeSpan ColdStartTarget = TimeSpan.FromSeconds(2);

    /// <summary>PRD 4 target: 60 fps while scrolling 100k rows, i.e. at most 16.7 ms per frame.</summary>
    public const double FrameTargetMilliseconds = 1000.0 / 60.0;

    /// <summary>Register sizes from which a scroll measurement counts as "100k rows".</summary>
    public const int LargeRegisterRows = 100_000;

    /// <summary>The review window.</summary>
    public const int ReviewWindowDays = 30;
}

/// <summary>Where the first-budget clock starts.</summary>
public enum FirstBudgetStart
{
    /// <summary>No start known (empty file).</summary>
    Unknown,

    /// <summary>The first launch of Keel on this computer (settings.json).</summary>
    FirstLaunch,

    /// <summary>The first change recorded in the budget file (Keel was installed before this measurement existed).</summary>
    FileCreated,
}

/// <summary>Time from first launch to first assigned budget.</summary>
/// <param name="StartedAt">When the clock started (UTC), or null.</param>
/// <param name="Start">What <paramref name="StartedAt"/> is.</param>
/// <param name="FirstAssignedAt">First money assigned to a category in this file (UTC), or null when none yet.</param>
public sealed record FirstBudgetStat(DateTime? StartedAt, FirstBudgetStart Start, DateTime? FirstAssignedAt)
{
    /// <summary>The time taken, when both ends are known.</summary>
    public TimeSpan? Duration => StartedAt is { } start && FirstAssignedAt is { } end ? (end > start ? end - start : TimeSpan.Zero) : null;
}

/// <summary>Review approvals over the window.</summary>
/// <param name="Approved">Imported transactions approved in the window.</param>
/// <param name="Unchanged">Of those, approved with the category (or transfer) Keel suggested.</param>
/// <param name="Since">Start of the window (UTC).</param>
public sealed record ReviewAccuracyStat(int Approved, int Unchanged, DateTime Since)
{
    /// <summary>Share approved without a change, or null without approvals.</summary>
    public double? Rate => Approved == 0 ? null : (double)Unchanged / Approved;
}

/// <summary>Re-imports of files imported before.</summary>
/// <param name="Reimports">Number of re-imports seen.</param>
/// <param name="Rows">Rows in those files.</param>
/// <param name="Flagged">Rows flagged as duplicates and skipped.</param>
/// <param name="LastAt">Last re-import (UTC), or null.</param>
public sealed record ReimportStat(int Reimports, int Rows, int Flagged, DateTime? LastAt)
{
    /// <summary>Share flagged, or null without re-imports.</summary>
    public double? Rate => Rows == 0 ? null : (double)Flagged / Rows;
}
