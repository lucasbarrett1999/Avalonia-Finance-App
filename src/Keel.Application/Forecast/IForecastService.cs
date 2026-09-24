using Keel.Domain;
using Keel.Domain.Forecast;

namespace Keel.Application.Forecast;

/// <summary>
/// Cash-flow forecast (F-REP-4) on top of <see cref="ForecastEngine"/> (ADR 0033): loads cleared
/// balances, scheduled transactions, recurring items and 90 days of history and maps the result to
/// DTOs. Implemented in a later M5 task.
/// </summary>
public interface IForecastService
{
    /// <summary>Computes the forecast from today.</summary>
    Task<ForecastDto> GetForecastAsync(ForecastRequest request, CancellationToken ct);

    /// <summary>What lands on a day (for the chart tooltip and "show the math").</summary>
    Task<ForecastDayExplanationDto> ExplainDayAsync(ForecastRequest request, DateOnly date, Guid? accountId, CancellationToken ct);
}

/// <summary>Forecast settings from the Reports toolbar.</summary>
/// <param name="Days">Days to project (default 90).</param>
/// <param name="IncludeDiscretionarySpend">The average daily discretionary spend toggle.</param>
/// <param name="Floor">The user's floor, when set.</param>
/// <param name="AccountIds">Accounts to show; empty for every projected account.</param>
public sealed record ForecastRequest(int Days = 90, bool IncludeDiscretionarySpend = false, Money? Floor = null, IReadOnlyCollection<Guid>? AccountIds = null);

/// <summary>The forecast.</summary>
/// <param name="Start">Today.</param>
/// <param name="End">Last day.</param>
/// <param name="Accounts">One series per account.</param>
/// <param name="Combined">All accounts combined.</param>
/// <param name="Skipped">Sources left out, with the reason.</param>
/// <param name="Discretionary">Discretionary-spend computation per account.</param>
public sealed record ForecastDto(
    DateOnly Start,
    DateOnly End,
    IReadOnlyList<ForecastSeriesDto> Accounts,
    ForecastSeriesDto Combined,
    IReadOnlyList<ForecastSkip> Skipped,
    IReadOnlyList<DiscretionarySpend> Discretionary);

/// <summary>One series.</summary>
/// <param name="AccountId">Account, or null for combined.</param>
/// <param name="Name">Display name.</param>
/// <param name="Points">Closing balance per day.</param>
/// <param name="LowestDate">Date of the lowest balance.</param>
/// <param name="LowestBalance">Lowest balance.</param>
/// <param name="BelowFloor">Days below the floor.</param>
public sealed record ForecastSeriesDto(
    Guid? AccountId,
    string Name,
    IReadOnlyList<ForecastPointDto> Points,
    DateOnly LowestDate,
    Money LowestBalance,
    IReadOnlyList<ForecastPointDto> BelowFloor);

/// <summary>One day's closing balance.</summary>
/// <param name="Date">Date.</param>
/// <param name="Balance">Closing balance.</param>
/// <param name="HasEntries">Something lands on this day (chart markers).</param>
public sealed record ForecastPointDto(DateOnly Date, Money Balance, bool HasEntries);

/// <summary>"Show the math" for one day.</summary>
/// <param name="Date">Date.</param>
/// <param name="AccountId">Account, or null for combined.</param>
/// <param name="Opening">Opening balance.</param>
/// <param name="Entries">What lands on the day.</param>
/// <param name="Closing">Closing balance.</param>
public sealed record ForecastDayExplanationDto(DateOnly Date, Guid? AccountId, Money Opening, IReadOnlyList<ForecastEntryDto> Entries, Money Closing);

/// <summary>One entry of a day.</summary>
/// <param name="Kind">Scheduled, transfer, recurring or discretionary.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Label">Payee or description.</param>
/// <param name="Amount">Amount.</param>
/// <param name="SourceId">Scheduled transaction or recurring item.</param>
/// <param name="IsOverdue">Overdue occurrence landing today.</param>
/// <param name="DueDate">Own date of the occurrence.</param>
public sealed record ForecastEntryDto(ForecastEntryKind Kind, Guid AccountId, string Label, Money Amount, Guid? SourceId, bool IsOverdue, DateOnly DueDate);
