namespace Keel.Domain.Ledger;

/// <summary>Split validation (F-ACC-5): the split amounts must sum exactly to the parent amount.</summary>
public static class SplitRules
{
    /// <summary>What is left to allocate: parent amount minus the sum of the splits.</summary>
    public static long Remaining(long parentAmount, IEnumerable<long> splitAmounts)
    {
        ArgumentNullException.ThrowIfNull(splitAmounts);
        long sum = 0;
        foreach (var amount in splitAmounts)
        {
            sum = checked(sum + amount);
        }

        return checked(parentAmount - sum);
    }

    /// <summary>Validates a split set; returns null when valid, otherwise the problem.</summary>
    public static SplitProblem? Validate(long parentAmount, IReadOnlyList<long> splitAmounts)
    {
        ArgumentNullException.ThrowIfNull(splitAmounts);
        if (splitAmounts.Count < 2)
        {
            return SplitProblem.TooFewLines;
        }

        if (splitAmounts.Any(a => a == 0))
        {
            return SplitProblem.ZeroLine;
        }

        return Remaining(parentAmount, splitAmounts) == 0 ? null : SplitProblem.SumMismatch;
    }
}

/// <summary>Why a split set is invalid.</summary>
public enum SplitProblem
{
    /// <summary>A split needs at least two lines.</summary>
    TooFewLines,

    /// <summary>A line has a zero amount.</summary>
    ZeroLine,

    /// <summary>The lines do not sum to the parent amount.</summary>
    SumMismatch,
}
