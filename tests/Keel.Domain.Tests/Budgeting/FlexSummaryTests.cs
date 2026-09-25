using System.Globalization;
using System.Text;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using static VerifyXunit.Verifier;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>
/// The Flex view (F-BUD-6, ADR 0091) over the PRD 6.4.7 worked example: every number is a sum of the
/// calculator's own cells, the default kinds follow targets, and a user's tag always wins.
/// </summary>
public class FlexSummaryTests
{
    private static readonly IReadOnlyDictionary<Guid, TargetType> NoTargets = new Dictionary<Guid, TargetType>();

    // PRD 6.4.7 plus a Non-monthly category (Car insurance) and an extra Flex one (Dining).
    private static (BudgetBuilder B, Guid Groceries, Guid Rent, Guid PayVisa, Guid Insurance, Guid Dining) Example()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var groceries = b.Category("Groceries");
        var rent = b.Category("Rent");
        var insurance = b.Category("Car insurance");
        var dining = b.Category("Dining");
        var payVisa = b.PaymentCategory(visa);

        b.Txn("2026-08-01", checking, 3_000_00, b.Rta)
            .Assign(rent, "2026-08", 1_500_00)
            .Assign(groceries, "2026-08", 400_00)
            .Txn("2026-08-05", checking, -1_500_00, rent)
            .Txn("2026-08-10", visa, -250_00, groceries)
            .Txn("2026-08-20", visa, -200_00, groceries)
            .Transfer("2026-08-25", checking, visa, 100_00)
            .Assign(insurance, "2026-08", 100_00)
            .Assign(dining, "2026-08", 120_00)
            .Txn("2026-08-12", checking, -70_00, dining)
            .Assign(insurance, "2026-09", 100_00)
            .Assign(dining, "2026-09", 100_00)
            .Txn("2026-09-03", checking, -30_00, dining);
        return (b, groceries, rent, payVisa, insurance, dining);
    }

    [Fact]
    public Task Worked_example_6_4_7_in_the_flex_view()
    {
        var (b, groceries, rent, payVisa, insurance, dining) = Example();
        b.Tag(rent, FlexKind.Fixed).Tag(insurance, FlexKind.NonMonthly);
        var input = b.Build();
        var snapshot = BudgetCalculator.Compute(input, BudgetBuilder.M("2026-08"), BudgetBuilder.M("2026-09"));
        var kinds = FlexClassifier.Kinds(input.Categories, NoTargets);

        var aug = FlexSummary.Compute(snapshot.Month(BudgetBuilder.M("2026-08")), kinds);
        aug.Income.ShouldBe(3_000_00);                                   // InflowRTA(2026-08)
        aug.Fixed.CategoryIds.ShouldBe([rent]);
        (aug.Fixed.Assigned, aug.Fixed.Activity, aug.Fixed.Available).ShouldBe((1_500_00, -1_500_00, 0));
        aug.NonMonthly.CategoryIds.ShouldBe([insurance]);
        aug.NonMonthly.Assigned.ShouldBe(100_00);
        aug.Flex.CategoryIds.ShouldBe([groceries, dining]);              // untagged, no target: Flex
        aug.Flex.Assigned.ShouldBe(520_00);                              // 400 + 120
        aug.Flex.Activity.ShouldBe(-520_00);                             // −450 − 70
        aug.Flex.Spent.ShouldBe(520_00);
        aug.Flex.Available.ShouldBe(0);                                  // Groceries −50 (yellow) + Dining 50
        aug.CardPaymentCategoryIds.ShouldBe([payVisa]);                  // in no bucket

        var sep = FlexSummary.Compute(snapshot.Month(BudgetBuilder.M("2026-09")), kinds);
        sep.Income.ShouldBe(0);
        sep.Flex.Carry.ShouldBe(50_00);                                  // Groceries' −50 does not carry; Dining's 50 does
        sep.Flex.Budgeted.ShouldBe(150_00);                              // carry 50 + assigned 100
        sep.Flex.Available.ShouldBe(120_00);
        sep.NonMonthly.Available.ShouldBe(200_00);                       // saved so far

        return Verify(Print(b, aug, sep));
    }

    [Fact]
    public void Every_bucket_is_a_sum_of_grid_cells_and_the_buckets_add_up_to_the_visible_group_rows()
    {
        var (b, _, rent, _, insurance, _) = Example();
        b.Tag(rent, FlexKind.Fixed).Tag(insurance, FlexKind.NonMonthly);
        var input = b.Build();
        var month = BudgetCalculator.Compute(input, BudgetBuilder.M("2026-08"), BudgetBuilder.M("2026-08")).Month(BudgetBuilder.M("2026-08"));
        var summary = FlexSummary.Compute(month, FlexClassifier.Kinds(input.Categories, NoTargets));

        foreach (var bucket in new[] { summary.Fixed, summary.NonMonthly, summary.Flex })
        {
            var cells = bucket.CategoryIds.Select(month.Category).ToList();
            bucket.Assigned.ShouldBe(cells.Sum(c => c.Assigned));
            bucket.Activity.ShouldBe(cells.Sum(c => c.Activity));
            bucket.Available.ShouldBe(cells.Sum(c => c.Available));
            bucket.Carry.ShouldBe(cells.Sum(c => c.Carry));
            bucket.Available.ShouldBe(bucket.Carry + bucket.Assigned + bucket.Activity);
        }

        var cards = summary.CardPaymentCategoryIds.Select(month.Category).ToList();
        var visibleGroups = month.Groups.Where(g => !g.IsHidden).ToList();
        (summary.Fixed.Assigned + summary.NonMonthly.Assigned + summary.Flex.Assigned + cards.Sum(c => c.Assigned)).ShouldBe(visibleGroups.Sum(g => g.Assigned));
        (summary.Fixed.Available + summary.NonMonthly.Available + summary.Flex.Available + cards.Sum(c => c.Available)).ShouldBe(visibleGroups.Sum(g => g.Available));
    }

    [Fact]
    public void Generated_budgets_split_every_visible_regular_category_into_exactly_one_bucket()
    {
        for (var seed = 1; seed <= 5; seed++)
        {
            var input = BudgetInputGenerator.Generate(months: 6, categories: 40, accounts: 6, seed: seed, density: 0.4);
            var kinds = input.Categories.Where(c => c.Kind == BudgetCategoryKind.Regular)
                .Select((c, i) => (c.Id, Kind: (FlexKind)(1 + (i % 3))))
                .ToDictionary(x => x.Id, x => x.Kind);
            var snapshot = BudgetCalculator.Compute(input, new DateOnly(2024, 1, 1), new DateOnly(2024, 6, 1));
            foreach (var month in snapshot.Months)
            {
                var summary = FlexSummary.Compute(month, kinds);
                var ids = summary.Fixed.CategoryIds.Concat(summary.NonMonthly.CategoryIds).Concat(summary.Flex.CategoryIds).ToList();
                ids.ShouldBeUnique();
                ids.OrderBy(i => i).ShouldBe(month.Categories.Where(c => c.IsVisible && c.Kind == BudgetCategoryKind.Regular).Select(c => c.CategoryId).OrderBy(i => i));
                summary.Fixed.CategoryIds.ShouldAllBe(id => kinds[id] == FlexKind.Fixed);
                var visible = month.Groups.Where(g => !g.IsHidden).Sum(g => g.Activity);
                var cards = summary.CardPaymentCategoryIds.Sum(id => month.Category(id).Activity);
                (summary.Fixed.Activity + summary.NonMonthly.Activity + summary.Flex.Activity + cards).ShouldBe(visible);
            }
        }
    }

    [Theory]
    [InlineData(null, FlexKind.Unset, FlexKind.Flex)]
    [InlineData(TargetType.MonthlySetAside, FlexKind.Unset, FlexKind.Fixed)]
    [InlineData(TargetType.MonthlySpending, FlexKind.Unset, FlexKind.Fixed)]
    [InlineData(TargetType.DebtPayment, FlexKind.Unset, FlexKind.Fixed)]
    [InlineData(TargetType.SavingsBalanceByDate, FlexKind.Unset, FlexKind.NonMonthly)]
    [InlineData(TargetType.MonthlySetAside, FlexKind.Flex, FlexKind.Flex)]            // a user's tag always wins
    [InlineData(TargetType.SavingsBalanceByDate, FlexKind.Fixed, FlexKind.Fixed)]
    [InlineData(null, FlexKind.NonMonthly, FlexKind.NonMonthly)]
    public void Untagged_categories_default_from_their_target_and_tags_are_never_overridden(TargetType? target, FlexKind tag, FlexKind expected) =>
        FlexClassifier.Effective(tag, target).ShouldBe(expected);

    [Fact]
    public void Defaults_from_targets_reach_the_summary_and_card_payment_categories_get_no_kind()
    {
        var (b, groceries, rent, payVisa, insurance, dining) = Example();
        b.Tag(dining, FlexKind.Fixed);
        var input = b.Build();
        var targets = new Dictionary<Guid, TargetType>
        {
            [rent] = TargetType.MonthlySetAside,
            [insurance] = TargetType.SavingsBalanceByDate,
            [dining] = TargetType.SavingsBalanceByDate,                  // tagged Fixed: the tag wins
        };
        var kinds = FlexClassifier.Kinds(input.Categories, targets);
        kinds[rent].ShouldBe(FlexKind.Fixed);
        kinds[insurance].ShouldBe(FlexKind.NonMonthly);
        kinds[dining].ShouldBe(FlexKind.Fixed);
        kinds[groceries].ShouldBe(FlexKind.Flex);
        kinds.ShouldNotContainKey(payVisa);
        kinds.ShouldNotContainKey(SystemIds.ReadyToAssignCategory);

        var month = BudgetCalculator.Compute(input, BudgetBuilder.M("2026-08"), BudgetBuilder.M("2026-08")).Month(BudgetBuilder.M("2026-08"));
        var summary = FlexSummary.Compute(month, kinds);
        summary.Fixed.CategoryIds.ShouldBe([rent, dining]);
        summary.Flex.CategoryIds.ShouldBe([groceries]);
    }

    [Fact]
    public void Hidden_categories_are_left_out_like_in_group_rows()
    {
        var (b, groceries, _, _, _, dining) = Example();
        b.Hide(dining);
        var input = b.Build();
        var month = BudgetCalculator.Compute(input, BudgetBuilder.M("2026-08"), BudgetBuilder.M("2026-08")).Month(BudgetBuilder.M("2026-08"));
        var summary = FlexSummary.Compute(month, FlexClassifier.Kinds(input.Categories, NoTargets));
        summary.Flex.CategoryIds.ShouldNotContain(dining);
        summary.Flex.CategoryIds.ShouldContain(groceries);
    }

    [Theory]
    [InlineData("2026-09-01", 30, 30, 4_00, 0.0)]          // first day: the whole month is left
    [InlineData("2026-09-18", 13, 30, 9_23, 17.0 / 30)]    // today counts as a day left; rounded down
    [InlineData("2026-09-30", 1, 30, 120_00, 29.0 / 30)]
    [InlineData("2026-08-15", 30, 30, 4_00, 0.0)]          // a future month: every day is left
    [InlineData("2026-10-02", 0, 30, 0, 1.0)]              // a past month: nothing to pace
    public void Pace_counts_days_left_including_today_and_divides_what_is_left(string today, int daysLeft, int days, long perDay, double elapsed)
    {
        var summary = new FlexSummary(new DateOnly(2026, 9, 1), 0, Empty(FlexKind.Fixed), Empty(FlexKind.NonMonthly),
            new FlexBucket(FlexKind.Flex, 0, 200_00, -80_00, 120_00, []), []);
        var pace = summary.Pace(DateOnly.Parse(today, CultureInfo.InvariantCulture));
        pace.DaysLeft.ShouldBe(daysLeft);
        pace.DaysInMonth.ShouldBe(days);
        pace.SafePerDay.ShouldBe(perDay);
        pace.MonthElapsed.ShouldBe(elapsed, 1e-9);
    }

    [Fact]
    public void Overspent_flex_is_safe_to_spend_nothing_and_shows_a_full_bar()
    {
        var over = new FlexBucket(FlexKind.Flex, 0, 100_00, -130_00, -30_00, []);
        over.SpentShare.ShouldBe(1.3, 1e-9);
        var summary = new FlexSummary(new DateOnly(2026, 9, 1), 0, Empty(FlexKind.Fixed), Empty(FlexKind.NonMonthly), over, []);
        summary.Pace(new DateOnly(2026, 9, 10)).SafePerDay.ShouldBe(0);
        new FlexBucket(FlexKind.Flex, 0, 0, 0, 0, []).SpentShare.ShouldBe(0);
        new FlexBucket(FlexKind.Flex, 0, 0, -5_00, -5_00, []).SpentShare.ShouldBe(1);
        new FlexBucket(FlexKind.Flex, 0, 50_00, 20_00, 70_00, []).Spent.ShouldBe(0);   // refunds are not negative spending
    }

    private static FlexBucket Empty(FlexKind kind) => new(kind, 0, 0, 0, 0, []);

    private static string Print(BudgetBuilder b, params FlexSummary[] months)
    {
        var text = new StringBuilder();
        string M(long amount) => (amount / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture);
        foreach (var s in months)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"== {s.Month:yyyy-MM} ==");
            text.AppendLine(CultureInfo.InvariantCulture, $"Income {M(s.Income)}");
            foreach (var bucket in new[] { s.Fixed, s.NonMonthly, s.Flex })
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{bucket.Kind,-10} carry {M(bucket.Carry),10} assigned {M(bucket.Assigned),10} activity {M(bucket.Activity),10} available {M(bucket.Available),10} "
                    + $"budgeted {M(bucket.Budgeted),10} spent {M(bucket.Spent),10} share {bucket.SpentShare:0.000}: {string.Join(", ", bucket.CategoryIds.Select(b.NameOf))}");
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"Card payments (no bucket): {string.Join(", ", s.CardPaymentCategoryIds.Select(b.NameOf))}");
            var pace = s.Pace(new DateOnly(2026, 9, 18));
            text.AppendLine(CultureInfo.InvariantCulture, $"Pace on 2026-09-18: {pace.DaysLeft} of {pace.DaysInMonth} days left, safe per day {M(pace.SafePerDay)}, elapsed {pace.MonthElapsed:0.000}");
            text.AppendLine();
        }

        return text.ToString();
    }
}
