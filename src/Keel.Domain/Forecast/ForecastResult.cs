namespace Keel.Domain.Forecast;

/// <summary>What produced a forecast entry.</summary>
public enum ForecastEntryKind
{
    /// <summary>An occurrence of a scheduled transaction.</summary>
    Scheduled,

    /// <summary>The other side of a scheduled transfer.</summary>
    ScheduledTransfer,

    /// <summary>An expected occurrence of a confirmed recurring item.</summary>
    Recurring,

    /// <summary>The average daily discretionary spend.</summary>
    Discretionary,
}

/// <summary>One amount landing on one account on one day.</summary>
/// <param name="Date">Day it lands on.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Kind">Source kind.</param>
/// <param name="Amount">Signed amount.</param>
/// <param name="SourceId">Scheduled transaction or recurring item; null for discretionary spend.</param>
/// <param name="PayeeId">Payee.</param>
/// <param name="Label">Display label.</param>
/// <param name="IsOverdue">An occurrence due before today that has not been entered; it lands on day 0.</param>
/// <param name="DueDate">The occurrence's own date (differs from <paramref name="Date"/> when overdue).</param>
/// <param name="CounterpartAccountId">For transfers: the other account.</param>
public sealed record ForecastEntry(
    DateOnly Date,
    Guid AccountId,
    ForecastEntryKind Kind,
    long Amount,
    Guid? SourceId,
    Guid? PayeeId,
    string? Label,
    bool IsOverdue,
    DateOnly DueDate,
    Guid? CounterpartAccountId = null);

/// <summary>One day of a series.</summary>
/// <param name="Date">Date.</param>
/// <param name="Opening">Balance before the day's entries.</param>
/// <param name="Change">Sum of the day's entries.</param>
/// <param name="Balance">Closing balance.</param>
public sealed record ForecastDay(DateOnly Date, long Opening, long Change, long Balance);

/// <summary>The projection of one account, or of all projected accounts combined.</summary>
/// <param name="AccountId">Account, or null for the combined series.</param>
/// <param name="Name">Account name, or "All cash accounts".</param>
/// <param name="StartingBalance">Cleared balance at the start of day 0.</param>
/// <param name="DailyDiscretionarySpend">Amount subtracted each future day (0 when the toggle is off).</param>
/// <param name="Days">Day 0 (today) through the last forecast day.</param>
/// <param name="Lowest">The day with the lowest closing balance (earliest on ties).</param>
/// <param name="BelowFloor">Days whose closing balance is below the floor (empty without a floor).</param>
public sealed record ForecastSeries(
    Guid? AccountId,
    string Name,
    long StartingBalance,
    long DailyDiscretionarySpend,
    IReadOnlyList<ForecastDay> Days,
    ForecastDay Lowest,
    IReadOnlyList<ForecastDay> BelowFloor);

/// <summary>Where a scheduled transaction or recurring item came from.</summary>
public enum ForecastSourceKind
{
    /// <summary>Scheduled transaction.</summary>
    Scheduled,

    /// <summary>Recurring item.</summary>
    Recurring,
}

/// <summary>Why a source contributes nothing.</summary>
public enum ForecastSkipReason
{
    /// <summary>The recurring item is not confirmed (only Active items count).</summary>
    NotConfirmed,

    /// <summary>A scheduled transaction already covers this payee and account (double-count guard).</summary>
    CoveredBySchedule,

    /// <summary>The recurring item has no account.</summary>
    NoAccount,

    /// <summary>Neither account is an open, on-budget cash account.</summary>
    NotCashAccount,
}

/// <summary>A source left out of the forecast, and why.</summary>
/// <param name="SourceId">Scheduled transaction or recurring item.</param>
/// <param name="Source">Its kind.</param>
/// <param name="Reason">Why.</param>
/// <param name="Label">Display label.</param>
public sealed record ForecastSkip(Guid SourceId, ForecastSourceKind Source, ForecastSkipReason Reason, string? Label);

/// <summary>How the average daily discretionary spend of an account was computed.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="From">First day of the window.</param>
/// <param name="To">Last day of the window (yesterday).</param>
/// <param name="Days">Days in the window.</param>
/// <param name="TotalSpend">Sum of the counted outflows, as a positive amount.</param>
/// <param name="TransactionCount">Outflows counted.</param>
/// <param name="ExcludedCount">Outflows excluded as transfers, scheduled or recurring.</param>
/// <param name="Daily">TotalSpend / Days, rounded half to even.</param>
public sealed record DiscretionarySpend(Guid AccountId, DateOnly From, DateOnly To, int Days, long TotalSpend, int TransactionCount, int ExcludedCount, long Daily);

/// <summary>The breakdown of one forecast day ("show the math", principle 7).</summary>
/// <param name="Date">Date.</param>
/// <param name="AccountId">Account, or null for all projected accounts.</param>
/// <param name="Opening">Balance before the day's entries.</param>
/// <param name="Entries">What lands on the day.</param>
/// <param name="Closing">Opening plus the entries.</param>
public sealed record ForecastDayExplanation(DateOnly Date, Guid? AccountId, long Opening, IReadOnlyList<ForecastEntry> Entries, long Closing);

/// <summary>The cash-flow forecast (F-REP-4).</summary>
/// <param name="Start">Day 0 (today).</param>
/// <param name="End">Last forecast day.</param>
/// <param name="Floor">The floor used for <see cref="ForecastSeries.BelowFloor"/>.</param>
/// <param name="Accounts">One series per projected account, in input order.</param>
/// <param name="Combined">All projected accounts together.</param>
/// <param name="Entries">Every entry, by date, then account order, then kind.</param>
/// <param name="Skipped">Sources that contribute nothing, with the reason.</param>
/// <param name="Discretionary">Discretionary-spend computation per account (empty when the toggle is off).</param>
public sealed record ForecastResult(
    DateOnly Start,
    DateOnly End,
    long? Floor,
    IReadOnlyList<ForecastSeries> Accounts,
    ForecastSeries Combined,
    IReadOnlyList<ForecastEntry> Entries,
    IReadOnlyList<ForecastSkip> Skipped,
    IReadOnlyList<DiscretionarySpend> Discretionary)
{
    /// <summary>The series of an account.</summary>
    public ForecastSeries Series(Guid accountId) =>
        Accounts.FirstOrDefault(a => a.AccountId == accountId)
        ?? throw new ArgumentException("The account is not part of the forecast.", nameof(accountId));

    /// <summary>What lands on <paramref name="date"/> for an account, or for all accounts when null.</summary>
    public ForecastDayExplanation Explain(DateOnly date, Guid? accountId = null)
    {
        if (date < Start || date > End)
        {
            throw new ArgumentOutOfRangeException(nameof(date), date, "The date is outside the forecast.");
        }

        var series = accountId is { } id ? Series(id) : Combined;
        var day = series.Days[date.DayNumber - Start.DayNumber];
        var entries = Entries.Where(e => e.Date == date && (accountId is null || e.AccountId == accountId)).ToList();
        return new ForecastDayExplanation(date, accountId, day.Opening, entries, day.Balance);
    }
}
