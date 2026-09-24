namespace Keel.Domain.Budgeting;

/// <summary>Kinds of lines in a "How is this computed?" breakdown (PRD 1.1 principle 7, 9.3).</summary>
public enum ExplanationLineKind
{
    /// <summary>Available at the end of the previous month (informational).</summary>
    PreviousAvailable,

    /// <summary>Carry(c, M): the positive part of last month's Available (term).</summary>
    Carry,

    /// <summary>Assigned(c, M) (term).</summary>
    Assigned,

    /// <summary>Activity of the category on one account (term; <see cref="ExplanationLine.AccountId"/>).</summary>
    ActivityOnAccount,

    /// <summary>Card spending of the category on one credit account (informational).</summary>
    CardSpend,

    /// <summary>Covered(c, K, M): card spending backed by budgeted money, moved to the card's payment category (informational).</summary>
    CoveredOnCard,

    /// <summary>Credit part of the overspending (informational, ≤ 0).</summary>
    CreditOverspent,

    /// <summary>Cash part of the overspending (informational, ≤ 0; reduces next month's Ready to Assign).</summary>
    CashOverspent,

    /// <summary>Money moved into a payment category from a spending category (term; <see cref="ExplanationLine.CategoryId"/>).</summary>
    CoveredFromCategory,

    /// <summary>A payment to the card from a cash account (term, negative; <see cref="ExplanationLine.AccountId"/>).</summary>
    Payment,

    /// <summary>Ready to Assign: income categorized "Ready to Assign" up to and including the month (term).</summary>
    Inflow,

    /// <summary>Ready to Assign: amounts assigned up to and including the month (term, negative).</summary>
    AssignedThroughMonth,

    /// <summary>Ready to Assign: amounts assigned in later months (term, negative).</summary>
    AssignedInFuture,

    /// <summary>Ready to Assign: cash overspending of one earlier month (term, negative; <see cref="ExplanationLine.Month"/>).</summary>
    CashOverspentInMonth,
}

/// <summary>One line of a breakdown.</summary>
/// <param name="Kind">What the line is.</param>
/// <param name="Amount">Minor units.</param>
/// <param name="IsTerm">True when the line is a term of the sum; the terms add up to <see cref="BudgetExplanation.Total"/>.</param>
/// <param name="AccountId">Account the line refers to, if any.</param>
/// <param name="CategoryId">Category the line refers to, if any.</param>
/// <param name="Month">Month the line refers to, if not the explained month.</param>
public sealed record ExplanationLine(
    ExplanationLineKind Kind,
    long Amount,
    bool IsTerm,
    Guid? AccountId = null,
    Guid? CategoryId = null,
    DateOnly? Month = null);

/// <summary>How a number was computed: its terms (which sum to <see cref="Total"/>) and context lines.</summary>
/// <param name="CategoryId">Explained category, or null for Ready to Assign.</param>
/// <param name="Month">Month (first day).</param>
/// <param name="Total">The explained number (Available, or Ready to Assign).</param>
/// <param name="Lines">Lines in reading order.</param>
public sealed record BudgetExplanation(Guid? CategoryId, DateOnly Month, long Total, IReadOnlyList<ExplanationLine> Lines)
{
    /// <summary>Builds the Available breakdown of one cell.</summary>
    public static BudgetExplanation ForCategory(CategoryMonthResult cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var lines = new List<ExplanationLine>
        {
            new(ExplanationLineKind.PreviousAvailable, cell.PreviousAvailable, IsTerm: false, Month: cell.Month.AddMonths(-1)),
            new(ExplanationLineKind.Carry, cell.Carry, IsTerm: true),
            new(ExplanationLineKind.Assigned, cell.Assigned, IsTerm: true),
        };

        if (cell.Kind == BudgetCategoryKind.CreditCardPayment)
        {
            foreach (var covered in cell.CoveredFromCategories)
            {
                lines.Add(new(ExplanationLineKind.CoveredFromCategory, covered.Amount, IsTerm: true, AccountId: cell.CardAccountId, CategoryId: covered.CategoryId));
            }

            foreach (var payment in cell.PaymentsByAccount)
            {
                lines.Add(new(ExplanationLineKind.Payment, -payment.Amount, IsTerm: true, AccountId: payment.AccountId));
            }
        }
        else
        {
            foreach (var activity in cell.ActivityByAccount)
            {
                lines.Add(new(ExplanationLineKind.ActivityOnAccount, activity.Amount, IsTerm: true, AccountId: activity.AccountId));
            }

            for (var i = 0; i < cell.CardSpendByCard.Count; i++)
            {
                var spend = cell.CardSpendByCard[i];
                lines.Add(new(ExplanationLineKind.CardSpend, spend.Amount, IsTerm: false, AccountId: spend.AccountId));
                lines.Add(new(ExplanationLineKind.CoveredOnCard, cell.CoveredByCard[i].Amount, IsTerm: false, AccountId: spend.AccountId));
            }
        }

        if (cell.CreditOverspent != 0)
        {
            lines.Add(new(ExplanationLineKind.CreditOverspent, cell.CreditOverspent, IsTerm: false));
        }

        if (cell.CashOverspent != 0)
        {
            lines.Add(new(ExplanationLineKind.CashOverspent, cell.CashOverspent, IsTerm: false));
        }

        return new BudgetExplanation(cell.CategoryId, cell.Month, cell.Available, lines);
    }
}
