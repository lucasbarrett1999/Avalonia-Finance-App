namespace Keel.Domain.Budgeting;

/// <summary>
/// Flex-mode tags (F-BUD-6, ADR 0091): the kind a category counts as in the Flex view. A category the
/// user has not tagged (<see cref="FlexKind.Unset"/>) gets a default from its target; a user's tag is
/// never overridden.
/// </summary>
public static class FlexClassifier
{
    /// <summary>
    /// The automatic kind of an untagged category: a target that asks for the same amount every month
    /// (monthly set-aside, monthly spending, debt payment) is Fixed, a savings-by-date target is
    /// Non-monthly, and a category without a target is Flex.
    /// </summary>
    public static FlexKind Default(TargetType? target) => target switch
    {
        TargetType.MonthlySetAside or TargetType.MonthlySpending or TargetType.DebtPayment => FlexKind.Fixed,
        TargetType.SavingsBalanceByDate => FlexKind.NonMonthly,
        _ => FlexKind.Flex,
    };

    /// <summary>The kind a category counts as: its tag, or the default when it is untagged.</summary>
    public static FlexKind Effective(FlexKind tag, TargetType? target) => tag == FlexKind.Unset ? Default(target) : tag;

    /// <summary>The effective kind of every regular category (Inflow and Credit Card Payment categories have none).</summary>
    /// <param name="categories">Categories with their tags (<see cref="BudgetCategory.FlexTag"/>).</param>
    /// <param name="targets">Target type by category, for categories with a target.</param>
    public static IReadOnlyDictionary<Guid, FlexKind> Kinds(IEnumerable<BudgetCategory> categories, IReadOnlyDictionary<Guid, TargetType> targets)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(targets);
        var kinds = new Dictionary<Guid, FlexKind>();
        foreach (var category in categories.Where(c => c.Kind == BudgetCategoryKind.Regular))
        {
            kinds[category.Id] = Effective(category.FlexTag, targets.TryGetValue(category.Id, out var type) ? type : null);
        }

        return kinds;
    }
}

/// <summary>The categories of one Flex kind in one month, with the sums of their grid cells.</summary>
/// <param name="Kind">Fixed, Non-monthly or Flex.</param>
/// <param name="Carry">Σ Carry(c, M).</param>
/// <param name="Assigned">Σ Assigned(c, M).</param>
/// <param name="Activity">Σ Activity(c, M) (outflows negative).</param>
/// <param name="Available">Σ Available(c, M) = Carry + Assigned + Activity.</param>
/// <param name="CategoryIds">The categories, in display order.</param>
public sealed record FlexBucket(FlexKind Kind, long Carry, long Assigned, long Activity, long Available, IReadOnlyList<Guid> CategoryIds)
{
    /// <summary>Money for the month: Carry + Assigned (so <see cref="Available"/> = Budgeted − Spent when all activity is spending).</summary>
    public long Budgeted => Carry + Assigned;

    /// <summary>Net spending this month: −Activity, never below zero (refunds larger than spending count as none).</summary>
    public long Spent => Math.Max(0, -Activity);

    /// <summary>Spent as a share of Budgeted (0 when nothing is budgeted and nothing spent; above 1 when overspent).</summary>
    public double SpentShare => Budgeted > 0 ? (double)Spent / Budgeted : Spent > 0 ? 1 : 0;
}

/// <summary>Pace of the Flex bucket through a month.</summary>
/// <param name="DaysInMonth">Days in the month.</param>
/// <param name="DaysLeft">Days left including today (the whole month for a future month, 0 for a past one).</param>
/// <param name="SafePerDay">What can be spent per remaining day: max(0, Flex Available) / DaysLeft, rounded down (0 when no days are left).</param>
/// <param name="MonthElapsed">Share of the month before today (0 for a future month, 1 for a past one).</param>
public readonly record struct FlexPace(int DaysInMonth, int DaysLeft, long SafePerDay, double MonthElapsed);

/// <summary>
/// The one-number view of a month (F-BUD-6, ADR 0091): an alternate presentation of a
/// <see cref="BudgetMonthResult"/>. Every number is a sum of cells the grid shows; there is no second
/// budget math. Only visible regular categories count (as in group rows, 6.4.3); Credit Card Payment
/// categories are listed in <see cref="CardPaymentCategoryIds"/> and belong to no bucket, because their
/// activity is money moved from spending categories that are already counted.
/// </summary>
/// <param name="Month">Month (first day).</param>
/// <param name="Income">InflowRTA(M): money categorized to Ready to Assign this month (the Inflow group's activity).</param>
/// <param name="Fixed">Fixed categories.</param>
/// <param name="NonMonthly">Non-monthly categories (their Assigned is the month's set-aside).</param>
/// <param name="Flex">Flex categories: the one Flex number and its spending progress.</param>
/// <param name="CardPaymentCategoryIds">Visible Credit Card Payment categories (not in any bucket).</param>
public sealed record FlexSummary(
    DateOnly Month,
    long Income,
    FlexBucket Fixed,
    FlexBucket NonMonthly,
    FlexBucket Flex,
    IReadOnlyList<Guid> CardPaymentCategoryIds)
{
    /// <summary>The bucket of <paramref name="kind"/>.</summary>
    public FlexBucket Bucket(FlexKind kind) => kind switch
    {
        FlexKind.Fixed => Fixed,
        FlexKind.NonMonthly => NonMonthly,
        FlexKind.Flex => Flex,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unset is not a bucket."),
    };

    /// <summary>Groups the visible regular categories of <paramref name="month"/> by their effective kind.</summary>
    /// <param name="month">A month of the snapshot the grid shows.</param>
    /// <param name="kinds">Effective kind by category (<see cref="FlexClassifier.Kinds"/>); a category missing from it counts as Flex.</param>
    public static FlexSummary Compute(BudgetMonthResult month, IReadOnlyDictionary<Guid, FlexKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(kinds);
        var sums = new Dictionary<FlexKind, (long Carry, long Assigned, long Activity, long Available, List<Guid> Ids)>
        {
            [FlexKind.Fixed] = (0, 0, 0, 0, []),
            [FlexKind.NonMonthly] = (0, 0, 0, 0, []),
            [FlexKind.Flex] = (0, 0, 0, 0, []),
        };
        var cards = new List<Guid>();

        // Categories come in display order (group order, then category order), so the lists follow the grid.
        foreach (var cell in month.Categories.Where(c => c.IsVisible))
        {
            if (cell.Kind == BudgetCategoryKind.CreditCardPayment)
            {
                cards.Add(cell.CategoryId);
                continue;
            }

            if (cell.Kind != BudgetCategoryKind.Regular)
            {
                continue;
            }

            var kind = kinds.TryGetValue(cell.CategoryId, out var k) && k != FlexKind.Unset ? k : FlexKind.Flex;
            var s = sums[kind];
            s.Ids.Add(cell.CategoryId);
            sums[kind] = (s.Carry + cell.Carry, s.Assigned + cell.Assigned, s.Activity + cell.Activity, s.Available + cell.Available, s.Ids);
        }

        FlexBucket Bucket(FlexKind kind)
        {
            var s = sums[kind];
            return new FlexBucket(kind, s.Carry, s.Assigned, s.Activity, s.Available, s.Ids);
        }

        return new FlexSummary(month.Month, month.InflowThisMonth, Bucket(FlexKind.Fixed), Bucket(FlexKind.NonMonthly), Bucket(FlexKind.Flex), cards);
    }

    /// <summary>Days left and safe-to-spend per day of the Flex bucket, seen on <paramref name="today"/>.</summary>
    public FlexPace Pace(DateOnly today)
    {
        var days = DateTime.DaysInMonth(Month.Year, Month.Month);
        var current = BudgetMonth.Of(today);
        int left;
        double elapsed;
        if (current < Month)
        {
            (left, elapsed) = (days, 0);
        }
        else if (current > Month)
        {
            (left, elapsed) = (0, 1);
        }
        else
        {
            (left, elapsed) = (days - today.Day + 1, (double)(today.Day - 1) / days);
        }

        var safe = left > 0 && Flex.Available > 0 ? Flex.Available / left : 0;
        return new FlexPace(days, left, safe, elapsed);
    }
}
