namespace Keel.Domain.Ledger;

/// <summary>Reconciliation arithmetic (F-ACC-3). All amounts are minor units.</summary>
public static class ReconciliationMath
{
    /// <summary>
    /// Statement balance minus cleared balance. Zero means the account reconciles; otherwise it is
    /// the amount a balance-adjustment transaction must carry.
    /// </summary>
    public static long Difference(long clearedBalance, long statementBalance) => checked(statementBalance - clearedBalance);

    /// <summary>
    /// Cleared balance as of the statement date: the sum of cleared and reconciled transactions dated
    /// on or before it.
    /// </summary>
    public static long ClearedBalance(IEnumerable<(DateOnly Date, long Amount, TransactionStatus Status)> transactions, DateOnly statementDate)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        long sum = 0;
        foreach (var (date, amount, status) in transactions)
        {
            if (status != TransactionStatus.Uncleared && date <= statementDate)
            {
                sum = checked(sum + amount);
            }
        }

        return sum;
    }

    /// <summary>Whether a transaction is locked by finishing a reconciliation at <paramref name="statementDate"/>.</summary>
    public static bool IsLockedByFinish(DateOnly date, TransactionStatus status, DateOnly statementDate) =>
        status == TransactionStatus.Cleared && date <= statementDate;
}
