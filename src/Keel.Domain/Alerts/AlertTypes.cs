using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Domain.Entities;

namespace Keel.Domain.Alerts;

/// <summary>A recurring item as alert evaluation sees it.</summary>
/// <param name="Id">Item id.</param>
/// <param name="NormalizedPayee">Normalized payee (the detection group with the account).</param>
/// <param name="AccountId">Account, or null for an item not tied to an account (matches any).</param>
/// <param name="Cadence">Cadence.</param>
/// <param name="ExpectedAmount">Expected amount (signed minor units).</param>
/// <param name="IsVariableAmount">Variable-amount items (utilities) get no price-increase alerts.</param>
/// <param name="NextExpectedDate">Next expected date.</param>
/// <param name="LastSeenDate">Last occurrence.</param>
/// <param name="Status">Status.</param>
public sealed record AlertRecurringItem(
    Guid Id,
    string NormalizedPayee,
    Guid? AccountId,
    RecurrenceCadence Cadence,
    long ExpectedAmount,
    bool IsVariableAmount,
    DateOnly NextExpectedDate,
    DateOnly LastSeenDate,
    RecurringStatus Status)
{
    /// <summary>Projects a stored item.</summary>
    public static AlertRecurringItem From(RecurringItem item, string normalizedPayee)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new AlertRecurringItem(
            item.Id,
            normalizedPayee,
            item.AccountId,
            item.Cadence,
            item.ExpectedAmount,
            item.IsVariableAmount,
            item.NextExpectedDate,
            item.LastSeenDate,
            item.Status);
    }

    /// <summary>True when a transaction of this payee in <paramref name="accountId"/> belongs to the item.</summary>
    public bool Matches(string normalizedPayee, Guid accountId) =>
        string.Equals(NormalizedPayee, normalizedPayee, StringComparison.Ordinal) && (AccountId is null || AccountId == accountId);
}

/// <summary>
/// The kind-specific data of an alert, stored as <see cref="Alert.PayloadJson"/>. <see cref="Key"/>
/// makes evaluation idempotent: an alert is never proposed twice for the same key.
/// </summary>
/// <param name="Key">Idempotency key: kind, item and occurrence (see <see cref="AlertKeys"/>).</param>
/// <param name="Payee">Normalized payee.</param>
/// <param name="Date">Date of the triggering transaction.</param>
/// <param name="Amount">Amount of the triggering transaction, or the expected amount (minor units).</param>
/// <param name="PreviousAmount">Previous occurrence (price increase) or the trial charge (trial conversion).</param>
/// <param name="IncreaseBasisPoints">Price increase in basis points of the previous amount (1% = 100).</param>
/// <param name="ExpectedDate">Missing item: the date it was expected.</param>
/// <param name="DaysLate">Missing item: days past the expected date.</param>
/// <param name="Cadence">New item: detected cadence.</param>
/// <param name="NextExpectedDate">New item: next expected date.</param>
/// <param name="TrialTransactionId">Trial conversion: the $0 or trial charge.</param>
public sealed record AlertPayload(
    string Key,
    string? Payee = null,
    DateOnly? Date = null,
    long? Amount = null,
    long? PreviousAmount = null,
    long? IncreaseBasisPoints = null,
    DateOnly? ExpectedDate = null,
    int? DaysLate = null,
    RecurrenceCadence? Cadence = null,
    DateOnly? NextExpectedDate = null,
    Guid? TrialTransactionId = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes to the stored JSON (camelCase, nulls omitted, enums by name).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a stored payload; null when the JSON is not a payload with a key.</summary>
    public static AlertPayload? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AlertPayload>(json, Options);
            return string.IsNullOrEmpty(payload?.Key) ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>An alert the evaluator proposes; the alert service stores it.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Key">Idempotency key.</param>
/// <param name="RecurringItemId">Related item.</param>
/// <param name="TransactionId">Triggering transaction.</param>
/// <param name="Payload">Kind-specific data.</param>
public sealed record AlertProposal(AlertKind Kind, string Key, Guid? RecurringItemId, Guid? TransactionId, AlertPayload Payload)
{
    /// <summary>The entity to insert.</summary>
    public Alert ToEntity(DateTime createdAtUtc, Guid? id = null) => new()
    {
        Id = id ?? EntityIds.New(),
        Kind = Kind,
        RecurringItemId = RecurringItemId,
        TransactionId = TransactionId,
        CreatedAt = createdAtUtc,
        PayloadJson = Payload.ToJson(),
    };
}

/// <summary>Idempotency keys: one alert per (kind, item, occurrence).</summary>
public static class AlertKeys
{
    /// <summary>Price increase of an item, per triggering transaction.</summary>
    public static string PriceIncrease(Guid itemId, Guid transactionId) => $"PriceIncrease:{itemId:N}:{transactionId:N}";

    /// <summary>Missing item, per expected date.</summary>
    public static string MissingExpected(Guid itemId, DateOnly expected) =>
        $"MissingExpected:{itemId:N}:{expected.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>New item, once per item.</summary>
    public static string NewRecurring(Guid itemId) => $"NewRecurring:{itemId:N}";

    /// <summary>Trial conversion, per converting transaction.</summary>
    public static string TrialConversion(Guid transactionId) => $"TrialConversion:{transactionId:N}";

    /// <summary>The key of a stored alert (from its payload), or null for an alert without one.</summary>
    public static string? Of(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return AlertPayload.FromJson(alert.PayloadJson)?.Key;
    }
}
