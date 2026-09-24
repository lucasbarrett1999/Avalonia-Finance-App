using System.Globalization;
using System.Text;
using Keel.Application.Budget;
using Keel.Domain;
using static Keel.Infrastructure.Tests.Budgeting.TestLedger;
using static VerifyXunit.Verifier;

namespace Keel.Infrastructure.Tests.Budgeting;

/// <summary>
/// M2 exit criterion: an envelope budget run end-to-end for three months of fixture data through
/// the database and <see cref="IBudgetService"/>, with numbers matching the golden file.
/// </summary>
public sealed class EndToEndBudgetTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private TestLedger? _ledger;

    public void Dispose() => _ledger?.Dispose();

    [Fact]
    public async Task Three_months_end_to_end()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var savings = l.Account("Savings", AccountType.Savings);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var rent = l.Category("Rent");
        var groceries = l.Category("Groceries");
        var dining = l.Category("Dining");
        var vacation = l.Category("Vacation");
        var pay = l.PaymentCategories[visa];

        // July
        l.Txn("2026-07-01", checking, 4_000_00, Rta);
        l.Txn("2026-07-03", checking, -1_500_00, rent);
        l.Txn("2026-07-10", visa, -320_00, groceries);
        l.Txn("2026-07-18", visa, -30_00, dining);
        l.Txn("2026-07-25", checking, -210_00, dining);
        l.Transfer("2026-07-28", checking, visa, 200_00);

        // August
        l.Txn("2026-08-01", checking, 4_000_00, Rta);
        l.Txn("2026-08-03", checking, -1_500_00, rent);
        l.Txn("2026-08-12", visa, -450_00, groceries);
        l.Txn("2026-08-20", visa, 50_00, groceries);             // refund
        l.Txn("2026-08-22", visa, -260_00, dining);
        l.Transfer("2026-08-28", checking, visa, 470_00);

        // September
        l.Txn("2026-09-01", checking, 4_000_00, Rta);
        l.Txn("2026-09-05", checking, -1_500_00, rent);
        l.Txn("2026-09-09", checking, -200_00, groceries);
        l.Txn("2026-09-09", visa, -100_00, groceries);
        l.Transfer("2026-09-10", checking, savings, 1_000_00);  // on budget ↔ on budget
        await l.SaveAsync();

        var service = l.Service();
        foreach (var (category, amount) in new[] { (rent, 1_500_00L), (groceries, 500_00L), (dining, 200_00L), (vacation, 300_00L) })
        {
            await service.AssignAsync(category, M("2026-07"), amount, Ct);
        }

        foreach (var (category, amount) in new[] { (rent, 1_500_00L), (groceries, 400_00L), (dining, 250_00L), (vacation, 300_00L) })
        {
            await service.AssignAsync(category, M("2026-08"), amount, Ct);
        }

        await service.SetTargetAsync(new TargetDto(rent, TargetType.MonthlySetAside, 1_500_00), Ct);
        await service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySpending, 450_00), Ct);
        await service.SetTargetAsync(new TargetDto(vacation, TargetType.SavingsBalanceByDate, 1_500_00, D("2026-12-01")), Ct);
        var funded = await service.FundTargetsAsync(M("2026-09"), Ct);
        await service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-09"), vacation, dining, 50_00), Ct);

        var months = await service.GetRangeAsync(M("2026-07"), M("2026-09"), Ct);

        // Hand-computed from PRD 6.4: RTA(07) = 4,000 − 6,945 assigned in all months; RTA(08) = 8,000 − 6,945 − 10
        // (July Dining cash overspending: −40 with 30 of it on the card); RTA(09) = 12,000 − 6,945 − 10.
        funded.Funded.Amount.ShouldBe(1_500_00 + 270_00 + 225_00);
        funded.FullyFunded.ShouldBeTrue();
        months.Select(m => m.ReadyToAssign.Amount).ShouldBe([-2_945_00, 1_045_00, 5_045_00]);
        BudgetCategoryDto Row(int month, Guid id) => months[month].Groups.SelectMany(g => g.Categories).Single(c => c.Id == id);
        Row(0, dining).Available.Amount.ShouldBe(-40_00);
        Row(0, dining).Overspending.ShouldBe(OverspendingKind.Cash);
        Row(0, pay).Available.Amount.ShouldBe(120_00);
        Row(1, groceries).Available.Amount.ShouldBe(180_00);
        Row(1, dining).Overspending.ShouldBe(OverspendingKind.Credit);
        Row(1, pay).Available.Amount.ShouldBe(300_00);
        Row(1, pay).CardPayment!.CardBalance.Amount.ShouldBe(-340_00);
        Row(2, groceries).Available.Amount.ShouldBe(150_00);
        Row(2, dining).Available.Amount.ShouldBe(50_00);
        Row(2, vacation).Available.Amount.ShouldBe(775_00);
        Row(2, pay).Available.Amount.ShouldBe(400_00);
        Row(2, pay).CardPayment!.Difference.Amount.ShouldBe(-40_00);
        Row(2, vacation).Target!.Underfunded.Amount.ShouldBe(50_00);   // moved away after funding

        await Verify(Print(months));
    }

    private static string Money(Money m) => (m.Amount / 100m).ToString("#,##0.00;-#,##0.00;0.00", CultureInfo.InvariantCulture);

    private static string Print(IReadOnlyList<BudgetMonthDto> months)
    {
        var sb = new StringBuilder();
        foreach (var month in months)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"== {month.Month:yyyy-MM} ==  Ready to Assign {Money(month.ReadyToAssign)} ({month.ReadyToAssign.Currency}); assigned in future {Money(month.AssignedInFuture)}; uncategorized {Money(month.UncategorizedActivity)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Totals: assigned {Money(month.TotalAssigned)}, activity {Money(month.TotalActivity)}, available {Money(month.TotalAvailable)}");
            foreach (var group in month.Groups)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"[{group.Name}] assigned {Money(group.Assigned)}, activity {Money(group.Activity)}, available {Money(group.Available)}");
                foreach (var c in group.Categories)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  {c.Name,-12} carry {Money(c.Carry),9}  assigned {Money(c.Assigned),9}  activity {Money(c.Activity),9}  available {Money(c.Available),9}  {c.Overspending}");
                    if (c.Target is { } t)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"  target {t.Type} {Money(t.Amount)}: needed {Money(t.NeededThisMonth)}, underfunded {Money(t.Underfunded)}");
                    }

                    if (c.CardPayment is { } card)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"  covered {Money(card.Covered)}, payments {Money(card.Payments)}, card balance {Money(card.CardBalance)}, difference {Money(card.Difference)}");
                    }

                    sb.AppendLine();
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
