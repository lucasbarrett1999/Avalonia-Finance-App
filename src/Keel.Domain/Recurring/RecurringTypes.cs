using Keel.Domain.Import;
using Keel.Domain.Scheduling;

namespace Keel.Domain.Recurring;

/// <summary>A ledger transaction as recurring detection and alerts see it.</summary>
/// <param name="Id">Transaction id.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Date">Ledger date.</param>
/// <param name="Amount">Signed amount in minor units (outflows negative).</param>
/// <param name="NormalizedPayee">Payee normalized by <see cref="PayeeNormalizer"/>; the grouping key with the account.</param>
public sealed record RecurringTransaction(Guid Id, Guid AccountId, DateOnly Date, long Amount, string NormalizedPayee)
{
    /// <summary>Creates the record from a raw payee descriptor, normalizing it with <see cref="PayeeNormalizer"/>.</summary>
    public static RecurringTransaction FromRaw(Guid id, Guid accountId, DateOnly date, long amount, string? payeeRaw) =>
        new(id, accountId, date, amount, PayeeNormalizer.Normalize(payeeRaw));

    /// <summary>The detection group of this transaction.</summary>
    public RecurringGroupKey Key => new(NormalizedPayee, AccountId);
}

/// <summary>The detection group of PRD 6.6: normalized payee and account.</summary>
/// <param name="NormalizedPayee">Normalized payee.</param>
/// <param name="AccountId">Account.</param>
public readonly record struct RecurringGroupKey(string NormalizedPayee, Guid AccountId) : IComparable<RecurringGroupKey>
{
    /// <inheritdoc />
    public int CompareTo(RecurringGroupKey other)
    {
        var byPayee = string.CompareOrdinal(NormalizedPayee, other.NormalizedPayee);
        return byPayee != 0 ? byPayee : AccountId.CompareTo(other.AccountId);
    }
}

/// <summary>The gap statistics of one candidate cadence for a group (the "explain" of a detection).</summary>
/// <param name="Cadence">Candidate cadence.</param>
/// <param name="Fraction">Fraction of gaps inside the cadence's tolerance window (PRD 6.6 step 2).</param>
/// <param name="MeanResidualDays">Mean absolute distance, in days, of the dates from a fixed-rate schedule with the
/// cadence's nominal period; breaks ties between cadences with equal fractions (ADR 0031).</param>
public sealed record CadenceScore(RecurrenceCadence Cadence, double Fraction, double MeanResidualDays);

/// <summary>A recurring pattern found by <see cref="RecurringDetector"/> (PRD 6.6).</summary>
/// <param name="Key">Normalized payee and account.</param>
/// <param name="Cadence">Winning cadence.</param>
/// <param name="ExpectedAmount">Median of the last 6 occurrences (signed, minor units).</param>
/// <param name="AmountTolerance">max($2, 10% of the expected amount), minor units.</param>
/// <param name="IsVariableAmount">Some of the last 6 occurrences differ from the median by more than the tolerance.</param>
/// <param name="NextExpectedDate">Next expected occurrence (6.6 step 6, ADR 0031).</param>
/// <param name="LastSeenDate">Date of the latest occurrence.</param>
/// <param name="FirstSeenDate">Date of the earliest occurrence in the look-back window.</param>
/// <param name="Confidence">Cadence fraction × (1 if the amount is stable, else 0.8).</param>
/// <param name="CadenceFraction">Fraction of gaps that fit the winning cadence.</param>
/// <param name="OccurrenceCount">Occurrences used (same direction, non-zero, in the window).</param>
/// <param name="Rule">Recurrence rule that projects the item from <see cref="NextExpectedDate"/>.</param>
/// <param name="IsLapsed">No occurrence for more than one full period after the expected date: the pattern has
/// stopped (ADR 0031). Lapsed detections do not create items and end active ones.</param>
/// <param name="TransactionIds">Occurrences, oldest first.</param>
/// <param name="Scores">Every candidate cadence's statistics, in cadence order.</param>
public sealed record DetectedRecurringItem(
    RecurringGroupKey Key,
    RecurrenceCadence Cadence,
    long ExpectedAmount,
    long AmountTolerance,
    bool IsVariableAmount,
    DateOnly NextExpectedDate,
    DateOnly LastSeenDate,
    DateOnly FirstSeenDate,
    double Confidence,
    double CadenceFraction,
    int OccurrenceCount,
    RecurrenceRule Rule,
    bool IsLapsed,
    IReadOnlyList<Guid> TransactionIds,
    IReadOnlyList<CadenceScore> Scores)
{
    /// <summary>Normalized payee.</summary>
    public string NormalizedPayee => Key.NormalizedPayee;

    /// <summary>Account.</summary>
    public Guid AccountId => Key.AccountId;

    /// <summary>Inflow pattern (paycheck) rather than a bill.</summary>
    public bool IsInflow => ExpectedAmount > 0;
}

/// <summary>The candidate cadences of PRD 6.6 step 2 with their gap windows.</summary>
/// <param name="Cadence">Cadence.</param>
/// <param name="MinGap">Smallest gap in days that counts (inclusive).</param>
/// <param name="MaxGap">Largest gap in days that counts (inclusive).</param>
/// <param name="ToleranceDays">The PRD tolerance (± days).</param>
/// <param name="PeriodDays">Nominal period in whole days (next-date grace and lapse checks).</param>
/// <param name="NominalDays">Exact average period in days (365.25 / occurrences per year for calendar cadences).</param>
/// <param name="MinOccurrences">Occurrences required (6.6 step 3).</param>
public sealed record CadenceWindow(
    RecurrenceCadence Cadence,
    int MinGap,
    int MaxGap,
    int ToleranceDays,
    int PeriodDays,
    double NominalDays,
    int MinOccurrences)
{
    /// <summary>
    /// PRD 6.6 windows: weekly 7 ± 2, biweekly 14 ± 3, semimonthly 15/16 ± 2 (13–18; the PRD gives no
    /// tolerance, ADR 0031), monthly 30/31 ± 5 (25–36), quarterly 91 ± 10, yearly 365 ± 20.
    /// </summary>
    public static IReadOnlyList<CadenceWindow> All { get; } =
    [
        new(RecurrenceCadence.Weekly, 5, 9, 2, 7, 7, 3),
        new(RecurrenceCadence.Biweekly, 11, 17, 3, 14, 14, 3),
        new(RecurrenceCadence.Semimonthly, 13, 18, 2, 15, 365.25 / 24, 3),
        new(RecurrenceCadence.Monthly, 25, 36, 5, 30, 365.25 / 12, 3),
        new(RecurrenceCadence.Quarterly, 81, 101, 10, 91, 365.25 / 4, 3),
        new(RecurrenceCadence.Yearly, 345, 385, 20, 365, 365.25, 2),
    ];

    /// <summary>The window of a cadence.</summary>
    public static CadenceWindow For(RecurrenceCadence cadence) => All[(int)cadence];

    /// <summary>True when a gap in days fits this cadence.</summary>
    public bool Fits(int gapDays) => gapDays >= MinGap && gapDays <= MaxGap;
}
