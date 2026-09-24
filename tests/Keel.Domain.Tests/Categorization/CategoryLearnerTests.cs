using Keel.Domain.Categorization;
using Keel.Domain.Entities;
using static Keel.Domain.Tests.Categorization.LearnerTestData;

namespace Keel.Domain.Tests.Categorization;

public class CategoryLearnerTests
{
    private static LearnerModel Train(IEnumerable<LabeledExample> examples) =>
        CategoryLearner.Train(examples, categories: Catalog);

    [Fact]
    public void Explains_an_exact_payee_suggestion_in_the_prd_shape()
    {
        var model = Train(
        [
            .. Repeat(12, "TRADER JOE'S #552", Groceries),
            Ex("Trader Joe's", Household, -1299),
            .. Repeat(20, "WHOLE FOODS MARKET", Groceries),
            .. Repeat(10, "TARGET", Household, -2500),
        ]);

        var suggestion = model.Suggest(Txn("POS DEBIT TRADER JOE'S #558"), 3)[0];

        suggestion.Explanation.ShouldBe("Suggested because 12 of 13 past 'TRADER JOES' transactions were Groceries");
        suggestion.CategoryId.ShouldBe(Groceries);
        suggestion.CategoryName.ShouldBe("Groceries");
        suggestion.IsPrimary.ShouldBeTrue();
        suggestion.Basis.ShouldBe(SuggestionBasis.ExactPayee);
        suggestion.PayeeCategoryExamples.ShouldBe(12);
        suggestion.PayeeExamples.ShouldBe(13);
        suggestion.NormalizedPayee.ShouldBe("TRADER JOES");
        suggestion.Confidence.ShouldBeInRange(0.85, 1.0);
    }

    [Fact]
    public void Explains_a_token_based_suggestion_by_the_words_that_drove_it()
    {
        var model = Train(
        [
            .. Repeat(9, "WHOLE FOODS MARKET #10", Groceries),
            .. Repeat(5, "FOODS OF INDIA", Groceries),
            Ex("FOODS OF INDIA", Dining),
            .. Repeat(8, "KEY FOOD", Groceries),
            .. Repeat(10, "TARGET", Household, -2500),
            .. Repeat(10, "JOES PIZZA", Dining, -2400),
        ]);

        var prediction = model.Predict(Txn("WHOLE FOODS MKT HOBOKEN"));

        prediction.Outcome.ShouldBe(LearnerOutcome.Suggested);
        prediction.Basis.ShouldBe(SuggestionBasis.PayeeTokens);
        var suggestion = prediction.Suggestions[0];
        suggestion.CategoryId.ShouldBe(Groceries);
        suggestion.Tokens.ShouldBe([new TokenEvidence("WHOLE", 9, 9), new TokenEvidence("FOODS", 14, 15)]);
        suggestion.Explanation.ShouldBe("Suggested because 9 of 9 past transactions with 'WHOLE' and 14 of 15 with 'FOODS' in the payee were Groceries");
    }

    [Fact]
    public void A_payee_needs_three_examples_before_it_can_drive_a_suggestion()
    {
        var two = Train([.. Repeat(2, "BLUE BOTTLE COFFEE", Dining, -600), .. Repeat(10, "TARGET", Household)]);
        var prediction = two.Predict(Txn("BLUE BOTTLE COFFEE", -600));
        prediction.Outcome.ShouldBe(LearnerOutcome.NotEnoughPayeeHistory);
        prediction.Suggestions.ShouldBeEmpty();
        prediction.PayeeExamples.ShouldBe(2);
        prediction.Reason.ShouldBe("Not suggested: only 2 approved transactions from 'BLUE BOTTLE COFFEE' (at least 3 needed).");

        var three = two.WithExample(Ex("SQ *BLUE BOTTLE COFFEE", Dining, -650));
        var suggestion = three.Suggest(Txn("BLUE BOTTLE COFFEE", -600)).ShouldHaveSingleItem();
        suggestion.CategoryId.ShouldBe(Dining);
        suggestion.Explanation.ShouldBe("Suggested because 3 of 3 past 'BLUE BOTTLE COFFEE' transactions were Dining Out");
    }

    [Fact]
    public void An_unknown_payee_with_no_shared_words_is_not_suggested()
    {
        var model = Train([.. Repeat(10, "TARGET", Household), .. Repeat(10, "KEY FOOD", Groceries)]);
        var prediction = model.Predict(Txn("ZANZIBAR IMPORTS"));
        prediction.Outcome.ShouldBe(LearnerOutcome.NotEnoughPayeeHistory);
        prediction.Reason.ShouldBe("Not suggested: no approved transactions from 'ZANZIBAR IMPORTS' or with its words yet.");
    }

    [Fact]
    public void Never_suggests_below_60_percent()
    {
        var model = Train([.. Repeat(5, "TARGET", Household), .. Repeat(5, "TARGET", Groceries), .. Repeat(10, "KEY FOOD", Groceries)]);

        var prediction = model.Predict(Txn("TARGET"));

        prediction.Outcome.ShouldBe(LearnerOutcome.BelowThreshold);
        prediction.Suggestions.ShouldBeEmpty();
        prediction.TopCandidates.Count.ShouldBeGreaterThanOrEqualTo(2);
        prediction.TopCandidates[0].Confidence.ShouldBeLessThan(CategoryLearner.MinimumConfidence);
        prediction.Reason.ShouldStartWith("Not suggested: the best guess, ");
        prediction.Reason.ShouldEndWith(", is below 60%.");
    }

    [Fact]
    public void Every_primary_suggestion_on_the_fixture_is_at_least_60_percent_and_confidences_are_probabilities()
    {
        var history = LabeledHistoryGenerator.Generate(months: 12, seed: 3);
        var model = CategoryLearner.Train(history.Take(600), categories: LabeledHistoryGenerator.Catalog);
        foreach (var example in history.Skip(600))
        {
            var suggestions = model.Suggest(example.Transaction, 5);
            if (suggestions.Count == 0)
            {
                continue;
            }

            suggestions[0].Confidence.ShouldBeGreaterThanOrEqualTo(CategoryLearner.MinimumConfidence);
            suggestions.Count(s => s.IsPrimary).ShouldBe(1);
            suggestions.ShouldAllBe(s => s.Confidence >= 0 && s.Confidence <= 1);
            suggestions.Sum(s => s.Confidence).ShouldBeLessThanOrEqualTo(1.0 + 1e-9);
            suggestions.Select(s => s.Confidence).ShouldBeInOrder(SortDirection.Descending);
        }
    }

    [Fact]
    public void Alternatives_follow_the_primary_down_to_the_alternative_floor()
    {
        var model = Train([.. Repeat(8, "AMAZON", Household, -2000), .. Repeat(4, "AMAZON", Electronics, -2000), .. Repeat(10, "KEY FOOD", Groceries)]);

        var suggestions = model.Suggest(Txn("AMZN Mktp US*2K4AB1CD2", -2000), 5);

        suggestions.Count.ShouldBe(2);
        suggestions[0].CategoryId.ShouldBe(Household);
        suggestions[1].CategoryId.ShouldBe(Electronics);
        suggestions[1].IsPrimary.ShouldBeFalse();
        suggestions[1].Explanation.ShouldBe("Suggested because 4 of 12 past 'AMAZON' transactions were Electronics");
        model.Suggest(Txn("AMAZON", -2000), 1).Count.ShouldBe(1);
        model.Suggest(Txn("AMAZON", -2000), 0).ShouldBeEmpty();
        model.Predict(Txn("AMAZON", -2000), 0).Outcome.ShouldBe(LearnerOutcome.Suggested);
    }

    [Fact]
    public void Context_features_can_pick_the_less_frequent_category_of_a_split_payee_and_say_why()
    {
        var examples = new List<LabeledExample>();
        examples.AddRange(Repeat(10, "AMAZON", Household, -1800));
        examples.AddRange(Repeat(8, "AMAZON", Electronics, -45_000));
        examples.AddRange(Repeat(20, "BEST BUY", Electronics, -52_000));
        examples.AddRange(Repeat(20, "TARGET", Household, -1500));
        var model = Train(examples);

        var suggestion = model.Suggest(Txn("AMAZON", -48_000))[0];

        suggestion.CategoryId.ShouldBe(Electronics);
        suggestion.SupportingContext.ShouldContain("amount");
        suggestion.Explanation.ShouldStartWith("Suggested because 8 of 18 past 'AMAZON' transactions were Electronics; its amount");
        suggestion.Explanation.ShouldEndWith("fit Electronics better than Household");
    }

    [Fact]
    public void Restricted_categories_are_never_suggested_when_the_history_is_mixed()
    {
        var model = Train([.. Repeat(6, "VISA ONLINE PAYMENT", Groceries), .. Repeat(5, "VISA ONLINE PAYMENT", CardPayment), .. Repeat(3, "VISA ONLINE PAYMENT", Hidden)]);

        var prediction = model.Predict(Txn("VISA ONLINE PAYMENT"));

        prediction.TopCandidates.Where(c => c.CategoryId == CardPayment || c.CategoryId == Hidden).ShouldAllBe(c => c.Excluded);
        prediction.Suggestions.ShouldAllBe(s => s.CategoryId != CardPayment && s.CategoryId != Hidden);
    }

    [Fact]
    public void Restricted_categories_are_suggested_when_the_history_is_exclusively_there()
    {
        var model = Train([.. Repeat(6, "ACME CORP PAYROLL PPD ID: 123456789", SystemIds.ReadyToAssignCategory, 412_000), .. Repeat(10, "TARGET", Household)]);

        var suggestion = model.Suggest(Txn("ACME CORP PAYROLL PPD ID: 987654321", 412_000)).ShouldHaveSingleItem();

        suggestion.CategoryId.ShouldBe(SystemIds.ReadyToAssignCategory);
        suggestion.Explanation.ShouldBe("Suggested because 6 of 6 past 'ACME CORP PAYROLL' transactions were Ready to Assign");
    }

    [Fact]
    public void Ready_to_assign_is_restricted_even_without_a_catalog()
    {
        var model = CategoryLearner.Train([.. Repeat(9, "VENMO", Dining, -2000), .. Repeat(9, "VENMO", SystemIds.ReadyToAssignCategory, -2000)]);
        model.Predict(Txn("VENMO", -2000)).TopCandidates.Single(c => c.CategoryId == SystemIds.ReadyToAssignCategory).Excluded.ShouldBeTrue();
    }

    [Fact]
    public void Hiding_or_renaming_a_category_takes_effect_without_retraining()
    {
        var model = Train([.. Repeat(10, "KEY FOOD", Groceries), .. Repeat(10, "TARGET", Household)]);
        model.Suggest(Txn("KEY FOOD"))[0].CategoryName.ShouldBe("Groceries");

        var renamed = model.WithCategories([.. Catalog.Where(c => c.Id != Groceries), new LearnerCategory(Groceries, "Food at Home", false)]);
        renamed.Suggest(Txn("KEY FOOD"))[0].Explanation.ShouldBe("Suggested because 10 of 10 past 'KEY FOOD' transactions were Food at Home");

        var hidden = model.WithCategories([.. Catalog.Where(c => c.Id != Groceries), new LearnerCategory(Groceries, "Groceries", true)]);
        hidden.Suggest(Txn("KEY FOOD"))[0].CategoryId.ShouldBe(Groceries); // history is exclusively the (now hidden) category
        var mixed = hidden.WithExample(Ex("KEY FOOD", Household));
        mixed.Suggest(Txn("KEY FOOD")).ShouldAllBe(s => s.CategoryId != Groceries);
    }

    [Fact]
    public void An_empty_model_has_no_history()
    {
        var prediction = CategoryLearner.Empty().Predict(Txn("KEY FOOD"));
        prediction.Outcome.ShouldBe(LearnerOutcome.NoHistory);
        prediction.Reason.ShouldBe("No approved transactions to learn from yet.");
    }

    [Fact]
    public void Examples_need_a_category_and_options_must_be_in_range()
    {
        Should.Throw<ArgumentException>(() => CategoryLearner.Train([Ex("X", Guid.Empty)]));
        Should.Throw<ArgumentException>(() => CategoryLearner.Train([], new LearnerOptions { Smoothing = 0 }));
        Should.Throw<ArgumentException>(() => CategoryLearner.Train([], new LearnerOptions { MinimumAlternativeConfidence = 2 }));
    }

    [Fact]
    public void Removing_an_example_that_was_never_learned_throws()
    {
        var model = Train(Repeat(3, "KEY FOOD", Groceries));
        Should.Throw<InvalidOperationException>(() => model.WithoutExample(Ex("TARGET", Household)));
        Should.Throw<InvalidOperationException>(() => model.WithoutExample(Ex("TARGET", Groceries)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 1)]
    [InlineData(1023, 10)]
    [InlineData(1024, 11)]
    [InlineData(-2047, 11)]
    [InlineData(2048, 12)]
    [InlineData(long.MinValue, 64)]
    public void Amount_buckets_double_in_width(long amount, int bucket)
    {
        LearnerFeatures.AmountBucketOf(amount).ShouldBe(bucket);
    }

    [Theory]
    [InlineData("TRADER JOES BROOKLYN NY", new[] { "BROOKLYN", "JOES", "NY", "TRADER" })]
    [InlineData("THE HOME DEPOT", new[] { "DEPOT", "HOME" })]
    [InlineData("7-ELEVEN", new[] { "7-ELEVEN" })]
    [InlineData("JOE S PIZZA A", new[] { "JOE", "PIZZA" })]
    [InlineData("", new string[0])]
    public void Tokens_are_distinct_words_with_letters_minus_stop_words(string payee, string[] tokens)
    {
        LearnerFeatures.Tokenize(payee).ShouldBe(tokens);
    }
}
