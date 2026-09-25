namespace Keel.Application.Stats;

/// <summary>
/// Measurements kept in settings.json for the Stats page (PRD 4): they belong to this computer, not to a
/// budget file. Local only; never transmitted.
/// </summary>
public sealed record LocalStats
{
    /// <summary>First launch of Keel on this computer (UTC); null on installs older than the measurement.</summary>
    public DateTime? FirstLaunchAt { get; init; }

    /// <summary>The last measured cold start.</summary>
    public ColdStartSample? ColdStart { get; init; }

    /// <summary>The last register scroll measured over at least <see cref="StatsReport.LargeRegisterRows"/> rows.</summary>
    public RegisterScrollSample? RegisterScroll { get; init; }
}

/// <summary>A cold start: process start to the first interactive frame of the shell with the file loaded.</summary>
/// <param name="Milliseconds">Duration.</param>
/// <param name="Transactions">Transactions in the file that was opened.</param>
/// <param name="Encrypted">The file was encrypted (key derivation included).</param>
/// <param name="MeasuredAt">When (UTC).</param>
public sealed record ColdStartSample(long Milliseconds, int Transactions, bool Encrypted, DateTime MeasuredAt);

/// <summary>Frame times while scrolling a register.</summary>
/// <param name="Rows">Rows in the register.</param>
/// <param name="Frames">Frames measured.</param>
/// <param name="AverageMilliseconds">Mean frame time.</param>
/// <param name="P95Milliseconds">95th percentile frame time.</param>
/// <param name="MeasuredAt">When (UTC).</param>
public sealed record RegisterScrollSample(int Rows, int Frames, double AverageMilliseconds, double P95Milliseconds, DateTime MeasuredAt);
