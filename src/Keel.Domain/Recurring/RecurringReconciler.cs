using Keel.Domain.Entities;

namespace Keel.Domain.Recurring;

/// <summary>A stored recurring item together with the normalized name of its payee (the grouping key).</summary>
/// <param name="Item">The stored item (not modified by reconciliation).</param>
/// <param name="NormalizedPayee">Normalized payee name of <see cref="RecurringItem.PayeeId"/>.</param>
public sealed record ExistingRecurringItem(RecurringItem Item, string NormalizedPayee);

/// <summary>What to do with a detection.</summary>
public enum RecurringDecisionKind
{
    /// <summary>Insert a new <see cref="RecurringItem"/> with <see cref="RecurringDecision.After"/>.</summary>
    Create,

    /// <summary>Update the stored item with <see cref="RecurringDecision.After"/>.</summary>
    Update,

    /// <summary>Nothing to write.</summary>
    NoOp,
}

/// <summary>Why a decision was made.</summary>
public enum RecurringDecisionReason
{
    /// <summary>No stored item for the group: a new pattern.</summary>
    NewPattern,

    /// <summary>The stored item's detection fields changed.</summary>
    Changed,

    /// <summary>The stored item already matches the detection.</summary>
    Unchanged,

    /// <summary>The user dismissed the item; detection does not touch it.</summary>
    Dismissed,

    /// <summary>The pattern has stopped: no new item is created.</summary>
    Lapsed,

    /// <summary>The pattern has stopped: the active or detected item is ended.</summary>
    Ended,

    /// <summary>A dismissed item whose payee the user re-enabled for detection.</summary>
    Reenabled,

    /// <summary>An ended item whose pattern resumed; it returns as detected, awaiting confirmation.</summary>
    Resumed,
}

/// <summary>The values detection writes to a <see cref="RecurringItem"/>.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="Cadence">Cadence.</param>
/// <param name="ExpectedAmount">Expected amount (minor units, signed).</param>
/// <param name="AmountTolerance">Amount tolerance (minor units).</param>
/// <param name="IsVariableAmount">Variable amount.</param>
/// <param name="NextExpectedDate">Next expected date.</param>
/// <param name="LastSeenDate">Last occurrence.</param>
/// <param name="Confidence">Confidence in [0, 1].</param>
/// <param name="Status">Status.</param>
public sealed record RecurringItemValues(
    Guid? AccountId,
    RecurrenceCadence Cadence,
    long ExpectedAmount,
    long AmountTolerance,
    bool IsVariableAmount,
    DateOnly NextExpectedDate,
    DateOnly LastSeenDate,
    double Confidence,
    RecurringStatus Status)
{
    /// <summary>Reads the detection fields of a stored item.</summary>
    public static RecurringItemValues From(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new RecurringItemValues(
            item.AccountId,
            item.Cadence,
            item.ExpectedAmount,
            item.AmountTolerance,
            item.IsVariableAmount,
            item.NextExpectedDate,
            item.LastSeenDate,
            item.Confidence,
            item.Status);
    }

    /// <summary>Writes these values to an item; user fields (subscription flag, category, schedule link) are kept.</summary>
    public void ApplyTo(RecurringItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.AccountId = AccountId;
        item.Cadence = Cadence;
        item.ExpectedAmount = ExpectedAmount;
        item.AmountTolerance = AmountTolerance;
        item.IsVariableAmount = IsVariableAmount;
        item.NextExpectedDate = NextExpectedDate;
        item.LastSeenDate = LastSeenDate;
        item.Confidence = Confidence;
        item.Status = Status;
    }

    /// <summary>A new item with these values.</summary>
    public RecurringItem ToNewItem(Guid id, Guid payeeId)
    {
        var item = new RecurringItem { Id = id, PayeeId = payeeId };
        ApplyTo(item);
        return item;
    }

    /// <summary>Names of the fields that differ from <paramref name="other"/>.</summary>
    public IReadOnlyList<string> Diff(RecurringItemValues other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var changed = new List<string>();
        if (AccountId != other.AccountId)
        {
            changed.Add(nameof(AccountId));
        }

        if (Cadence != other.Cadence)
        {
            changed.Add(nameof(Cadence));
        }

        if (ExpectedAmount != other.ExpectedAmount)
        {
            changed.Add(nameof(ExpectedAmount));
        }

        if (AmountTolerance != other.AmountTolerance)
        {
            changed.Add(nameof(AmountTolerance));
        }

        if (IsVariableAmount != other.IsVariableAmount)
        {
            changed.Add(nameof(IsVariableAmount));
        }

        if (NextExpectedDate != other.NextExpectedDate)
        {
            changed.Add(nameof(NextExpectedDate));
        }

        if (LastSeenDate != other.LastSeenDate)
        {
            changed.Add(nameof(LastSeenDate));
        }

        if (Math.Abs(Confidence - other.Confidence) > 1e-9)
        {
            changed.Add(nameof(Confidence));
        }

        if (Status != other.Status)
        {
            changed.Add(nameof(Status));
        }

        return changed;
    }
}

/// <summary>A create, update or no-op decision for one detection.</summary>
/// <param name="Kind">Create, update or no-op.</param>
/// <param name="Reason">Why.</param>
/// <param name="Detection">The detection.</param>
/// <param name="ItemId">The stored item, or the id to give the new item; null for a no-op without an item.</param>
/// <param name="Before">Stored values, when an item exists.</param>
/// <param name="After">Values to write (create and update).</param>
/// <param name="ChangedFields">Fields that change (update).</param>
public sealed record RecurringDecision(
    RecurringDecisionKind Kind,
    RecurringDecisionReason Reason,
    DetectedRecurringItem Detection,
    Guid? ItemId,
    RecurringItemValues? Before,
    RecurringItemValues? After,
    IReadOnlyList<string> ChangedFields);

/// <summary>
/// Merges detections with stored <see cref="RecurringItem"/>s (PRD 6.6 step 7): one item per group,
/// updated rather than duplicated; user-dismissed items are left alone unless the user re-enabled
/// detection for the payee. Writes nothing; the caller applies the decisions.
/// </summary>
/// <remarks>
/// Status transitions (ADR 0031): new patterns are created as <see cref="RecurringStatus.Detected"/>;
/// Active and Detected keep their status, or become Ended when the detection is lapsed; Paused stays
/// Paused; an Ended item whose pattern resumed (a newer occurrence, not lapsed) returns as Detected;
/// a re-enabled Dismissed item returns as Detected. Only detection fields change; the subscription
/// flag, category and scheduled-transaction link are the user's.
/// </remarks>
public static class RecurringReconciler
{
    /// <summary>Decides, per detection, whether to create, update or leave a stored item.</summary>
    /// <param name="detections">Output of <see cref="RecurringDetector.Detect"/>.</param>
    /// <param name="existing">Every stored recurring item with its normalized payee (any status).</param>
    /// <param name="reenabledPayees">Normalized payees the user re-enabled for detection.</param>
    /// <param name="newId">Id factory for created items (default <see cref="EntityIds.New"/>).</param>
    public static IReadOnlyList<RecurringDecision> Reconcile(
        IReadOnlyList<DetectedRecurringItem> detections,
        IReadOnlyList<ExistingRecurringItem> existing,
        IReadOnlySet<string>? reenabledPayees = null,
        Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(existing);
        newId ??= EntityIds.New;

        var byGroup = new Dictionary<RecurringGroupKey, List<ExistingRecurringItem>>();
        var byPayeeWithoutAccount = new Dictionary<string, List<ExistingRecurringItem>>(StringComparer.Ordinal);
        foreach (var e in existing.OrderBy(e => e.Item.Id))
        {
            if (e.Item.AccountId is { } account)
            {
                Add(byGroup, new RecurringGroupKey(e.NormalizedPayee, account), e);
            }
            else
            {
                Add(byPayeeWithoutAccount, e.NormalizedPayee, e);
            }
        }

        var used = new HashSet<Guid>();
        var decisions = new List<RecurringDecision>(detections.Count);
        foreach (var detection in detections)
        {
            var match = Take(byGroup, detection.Key, used) ?? Take(byPayeeWithoutAccount, detection.NormalizedPayee, used);
            decisions.Add(match is null
                ? ForNew(detection, newId)
                : ForExisting(detection, match.Item, reenabledPayees?.Contains(detection.NormalizedPayee) == true));
        }

        return decisions;
    }

    private static RecurringDecision ForNew(DetectedRecurringItem detection, Func<Guid> newId)
    {
        if (detection.IsLapsed)
        {
            return new RecurringDecision(RecurringDecisionKind.NoOp, RecurringDecisionReason.Lapsed, detection, null, null, null, []);
        }

        var after = Values(detection, detection.AccountId, RecurringStatus.Detected);
        return new RecurringDecision(RecurringDecisionKind.Create, RecurringDecisionReason.NewPattern, detection, newId(), null, after, []);
    }

    private static RecurringDecision ForExisting(DetectedRecurringItem detection, RecurringItem item, bool reenabled)
    {
        var before = RecurringItemValues.From(item);
        RecurringStatus status;
        RecurringDecisionReason reason;
        switch (item.Status)
        {
            case RecurringStatus.Dismissed when !reenabled:
                return new RecurringDecision(RecurringDecisionKind.NoOp, RecurringDecisionReason.Dismissed, detection, item.Id, before, null, []);
            case RecurringStatus.Dismissed:
                (status, reason) = detection.IsLapsed
                    ? (RecurringStatus.Ended, RecurringDecisionReason.Reenabled)
                    : (RecurringStatus.Detected, RecurringDecisionReason.Reenabled);
                break;
            case RecurringStatus.Active or RecurringStatus.Detected when detection.IsLapsed:
                (status, reason) = (RecurringStatus.Ended, RecurringDecisionReason.Ended);
                break;
            case RecurringStatus.Ended when !detection.IsLapsed && detection.LastSeenDate > item.LastSeenDate:
                (status, reason) = (RecurringStatus.Detected, RecurringDecisionReason.Resumed);
                break;
            default:
                (status, reason) = (item.Status, RecurringDecisionReason.Changed);
                break;
        }

        var after = Values(detection, item.AccountId ?? detection.AccountId, status);
        var changed = after.Diff(before);
        return changed.Count == 0
            ? new RecurringDecision(RecurringDecisionKind.NoOp, RecurringDecisionReason.Unchanged, detection, item.Id, before, null, [])
            : new RecurringDecision(RecurringDecisionKind.Update, reason, detection, item.Id, before, after, changed);
    }

    private static RecurringItemValues Values(DetectedRecurringItem d, Guid? accountId, RecurringStatus status) =>
        new(accountId, d.Cadence, d.ExpectedAmount, d.AmountTolerance, d.IsVariableAmount, d.NextExpectedDate, d.LastSeenDate, d.Confidence, status);

    private static void Add<TKey>(Dictionary<TKey, List<ExistingRecurringItem>> map, TKey key, ExistingRecurringItem item)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(item);
    }

    private static ExistingRecurringItem? Take<TKey>(Dictionary<TKey, List<ExistingRecurringItem>> map, TKey key, HashSet<Guid> used)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            return null;
        }

        foreach (var e in list)
        {
            if (used.Add(e.Item.Id))
            {
                return e;
            }
        }

        return null;
    }
}
