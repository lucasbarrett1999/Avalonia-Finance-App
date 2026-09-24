namespace Keel.Domain.Ledger;

/// <summary>
/// Transfer rules (F-ACC-4, PRD 6.3). A transfer is two transactions that share a
/// <see cref="Entities.Transaction.TransferPairId"/>, one in each account, with opposite amounts.
/// </summary>
public static class TransferRules
{
    /// <summary>
    /// Whether the side of a transfer in an account with <paramref name="sideOnBudget"/> carries a
    /// category, given the other account's flag. Between two on-budget (or two tracking) accounts
    /// neither side has a category. Between an on-budget and a tracking account the money leaves
    /// (or enters) the budget, so the on-budget side must be categorized and the tracking side is not.
    /// </summary>
    public static bool SideRequiresCategory(bool sideOnBudget, bool otherOnBudget) => sideOnBudget && !otherOnBudget;

    /// <summary>Whether a transfer between the two accounts needs a category on one of its sides.</summary>
    public static bool TransferRequiresCategory(bool firstOnBudget, bool secondOnBudget) => firstOnBudget != secondOnBudget;

    /// <summary>The amount of the counterpart row: the same money seen from the other account.</summary>
    public static long CounterpartAmount(long amount) => checked(-amount);
}
