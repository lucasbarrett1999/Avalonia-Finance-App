using System.Globalization;
using System.Text;
using Keel.Domain.Budgeting;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>Renders a snapshot as a fixed-width text table for Verify golden files.</summary>
internal static class SnapshotPrinter
{
    public static string Print(BudgetSnapshot snapshot, BudgetBuilder names, params (Guid Category, string Month)[] explain)
    {
        var sb = new StringBuilder();
        foreach (var month in snapshot.Months)
        {
            PrintMonth(sb, month, names);
        }

        foreach (var month in snapshot.Months)
        {
            PrintExplanation(sb, snapshot.ExplainReadyToAssign(month.Month), names);
        }

        foreach (var (category, monthText) in explain)
        {
            PrintExplanation(sb, snapshot.Explain(category, BudgetBuilder.M(monthText)), names);
        }

        return sb.ToString();
    }

    public static string Money(long minor) =>
        (minor / 100m).ToString("#,##0.00;-#,##0.00;0.00", CultureInfo.InvariantCulture);

    private static void PrintMonth(StringBuilder sb, BudgetMonthResult month, BudgetBuilder names)
    {
        sb.Append(CultureInfo.InvariantCulture, $"== {month.Month:yyyy-MM} ==").AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"Ready to Assign {Money(month.ReadyToAssign)} = inflow {Money(month.InflowThroughMonth)} - assigned {Money(month.AssignedThroughMonth)} - assigned in future {Money(month.AssignedInFuture)} + earlier cash overspending {Money(month.CashOverspentBefore)}")
            .AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"Totals: assigned {Money(month.TotalAssigned)}, activity {Money(month.TotalActivity)}, available {Money(month.TotalAvailable)}; inflow this month {Money(month.InflowThisMonth)}; cash overspent this month {Money(month.CashOverspentThisMonth)}; uncategorized {Money(month.UncategorizedActivity)}")
            .AppendLine();
        sb.AppendLine(Row("Category", "Assigned", "Activity", "Carry", "Available", "CashOver", "CreditOver", "Covered", "Payments", "State"));
        var cells = month.Categories.ToDictionary(c => c.CategoryId);
        foreach (var group in month.Groups)
        {
            sb.AppendLine(Row(
                $"[{names.NameOf(group.GroupId)}]{(group.IsHidden ? " (hidden)" : string.Empty)}",
                Money(group.Assigned),
                Money(group.Activity),
                string.Empty,
                Money(group.Available),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
            foreach (var id in group.CategoryIds)
            {
                var c = cells[id];
                sb.AppendLine(Row(
                    "  " + names.NameOf(id) + (c.IsVisible ? string.Empty : " (hidden)"),
                    Money(c.Assigned),
                    Money(c.Activity),
                    Money(c.Carry),
                    Money(c.Available),
                    Money(c.CashOverspent),
                    Money(c.CreditOverspent),
                    Money(c.Covered),
                    c.Kind == BudgetCategoryKind.CreditCardPayment ? Money(c.Payments) : string.Empty,
                    c.Overspending.ToString()));
            }
        }

        sb.AppendLine();
    }

    private static void PrintExplanation(StringBuilder sb, BudgetExplanation explanation, BudgetBuilder names)
    {
        var title = explanation.CategoryId is { } id ? names.NameOf(id) : "Ready to Assign";
        sb.Append(CultureInfo.InvariantCulture, $"-- Explain {title} {explanation.Month:yyyy-MM} = {Money(explanation.Total)} --").AppendLine();
        foreach (var line in explanation.Lines)
        {
            var detail = new List<string>();
            if (line.AccountId is { } account)
            {
                detail.Add("account " + names.NameOf(account));
            }

            if (line.CategoryId is { } category)
            {
                detail.Add("category " + names.NameOf(category));
            }

            if (line.Month is { } m)
            {
                detail.Add(m.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            }

            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {(line.IsTerm ? "+" : " ")} {line.Kind,-22} {Money(line.Amount),12}  {string.Join(", ", detail)}").TrimEnd());
        }

        sb.AppendLine();
    }

    private static string Row(string name, params string[] values)
    {
        var sb = new StringBuilder();
        sb.Append(name.PadRight(28));
        for (var i = 0; i < values.Length; i++)
        {
            sb.Append(i == values.Length - 1 ? "  " + values[i] : values[i].PadLeft(11));
        }

        return sb.ToString().TrimEnd();
    }
}
