namespace Keel.Domain.Budgeting;

/// <summary>The result of <see cref="BudgetCalculator.Compute"/>: every month of the requested range.</summary>
public sealed class BudgetSnapshot
{
    private readonly Dictionary<int, BudgetMonthResult> _byMonth;
    private readonly Dictionary<(Guid, int), CategoryMonthResult> _cells;
    private readonly int _firstComputedIndex;
    private readonly long[] _cashOverspentByMonth;

    internal BudgetSnapshot(IReadOnlyList<BudgetMonthResult> months, int firstComputedIndex, long[] cashOverspentByMonth)
    {
        Months = months;
        _firstComputedIndex = firstComputedIndex;
        _cashOverspentByMonth = cashOverspentByMonth;
        _byMonth = months.ToDictionary(m => BudgetMonth.Index(m.Month));
        _cells = new Dictionary<(Guid, int), CategoryMonthResult>(months.Sum(m => m.Categories.Count));
        foreach (var month in months)
        {
            var index = BudgetMonth.Index(month.Month);
            foreach (var cell in month.Categories)
            {
                _cells[(cell.CategoryId, index)] = cell;
            }
        }
    }

    /// <summary>The months of the requested range, in order.</summary>
    public IReadOnlyList<BudgetMonthResult> Months { get; }

    /// <summary>The month containing <paramref name="month"/>.</summary>
    /// <exception cref="KeyNotFoundException">The month is outside the computed range.</exception>
    public BudgetMonthResult Month(DateOnly month) =>
        _byMonth.TryGetValue(BudgetMonth.Index(month), out var result)
            ? result
            : throw new KeyNotFoundException($"{BudgetMonth.Of(month):yyyy-MM} is outside the computed range.");

    /// <summary>The cell of <paramref name="categoryId"/> in the month containing <paramref name="month"/>.</summary>
    /// <exception cref="KeyNotFoundException">Unknown category, or month outside the range.</exception>
    public CategoryMonthResult Cell(Guid categoryId, DateOnly month) =>
        _cells.TryGetValue((categoryId, BudgetMonth.Index(month)), out var cell)
            ? cell
            : throw new KeyNotFoundException($"No budget cell for category {categoryId} in {BudgetMonth.Of(month):yyyy-MM}.");

    /// <summary>How Available of a cell is computed.</summary>
    public BudgetExplanation Explain(Guid categoryId, DateOnly month) => BudgetExplanation.ForCategory(Cell(categoryId, month));

    /// <summary>How Ready to Assign of a month is computed (6.4.1); the terms sum to RTA(M).</summary>
    public BudgetExplanation ExplainReadyToAssign(DateOnly month)
    {
        var result = Month(month);
        var lines = new List<ExplanationLine>
        {
            new(ExplanationLineKind.Inflow, result.InflowThroughMonth, IsTerm: true),
            new(ExplanationLineKind.AssignedThroughMonth, -result.AssignedThroughMonth, IsTerm: true),
            new(ExplanationLineKind.AssignedInFuture, -result.AssignedInFuture, IsTerm: true),
        };

        var index = BudgetMonth.Index(result.Month);
        for (var i = _firstComputedIndex; i < index; i++)
        {
            var overspent = _cashOverspentByMonth[i - _firstComputedIndex];
            if (overspent != 0)
            {
                lines.Add(new(ExplanationLineKind.CashOverspentInMonth, overspent, IsTerm: true, Month: BudgetMonth.FromIndex(i)));
            }
        }

        return new BudgetExplanation(null, result.Month, result.ReadyToAssign, lines);
    }
}
