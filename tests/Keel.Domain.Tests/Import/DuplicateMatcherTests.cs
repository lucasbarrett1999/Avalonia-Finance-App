using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

public class DuplicateMatcherTests
{
    private static readonly Guid Account = Guid.Parse("0190a1b2-0000-7000-8000-000000000001");
    private static readonly Guid OtherAccount = Guid.Parse("0190a1b2-0000-7000-8000-000000000002");
    private static readonly Guid Connection = Guid.Parse("0190a1b2-0000-7000-8000-0000000000c1");
    private static readonly DateOnly Jan5 = new(2026, 1, 5);

    private static DedupCandidate Existing(
        DateOnly date,
        long amount,
        string payee,
        TransactionSource source = TransactionSource.File,
        string? providerId = null,
        string? pendingId = null,
        Guid? account = null,
        Guid? connection = null,
        bool hasImportMatch = false,
        int n = 1) =>
        new(Guid.Parse($"0190a1b2-0000-7000-9000-{n:D12}"), account ?? Account, date, amount,
            PayeeNormalizer.Normalize(payee), source, providerId, pendingId, null, connection, hasImportMatch);

    private static IReadOnlyList<DedupResult> Classify(IReadOnlyList<ImportRow> rows, params DedupCandidate[] existing) =>
        DuplicateMatcher.Classify(Account, rows, existing);

    [Fact]
    public void Empty_history_inserts_everything()
    {
        var results = Classify([new(Jan5, -500, "SQ *COFFEE"), new(Jan5, -500, "SQ *COFFEE")]);

        results.Select(r => r.Decision).ShouldBe([DedupDecision.Insert, DedupDecision.Insert]);
        results[0].NormalizedPayee.ShouldBe("COFFEE");
        results[0].Fingerprint.ShouldBe(ImportFingerprint.Compute(Account, Jan5, -500, "COFFEE"));
        results[0].ExistingId.ShouldBeNull();
    }

    [Fact]
    public void Step1_provider_id_updates_in_place()
    {
        var existing = Existing(Jan5, -500, "COFFEE", providerId: "FIT1");
        var results = Classify(
            [new(Jan5, -500, "COFFEE", "FIT1"), new(Jan5.AddDays(1), -650, "COFFEE SHOP", "FIT1")], existing);

        results[0].Decision.ShouldBe(DedupDecision.UpdateByProviderId);
        results[0].ExistingId.ShouldBe(existing.Id);
        results[0].HasChanges.ShouldBeFalse();
        results[1].Decision.ShouldBe(DedupDecision.UpdateByProviderId);
        results[1].HasChanges.ShouldBeTrue();
    }

    [Fact]
    public void Step1_provider_ids_are_scoped_to_the_account_or_connection()
    {
        var elsewhere = Existing(Jan5, -500, "COFFEE", providerId: "FIT1", account: OtherAccount);
        Classify([new(Jan5, -500, "OTHER", "FIT1")], elsewhere)[0].Decision.ShouldBe(DedupDecision.Insert);

        var sameConnection = Existing(Jan5, -500, "COFFEE", providerId: "P1", account: OtherAccount, connection: Connection);
        DuplicateMatcher.Classify(Account, [new ImportRow(Jan5, -500, "OTHER", "P1")], [sameConnection], Connection)[0]
            .Decision.ShouldBe(DedupDecision.UpdateByProviderId);
    }

    [Fact]
    public void Step2_pending_to_posted_matches_pending_id_or_provider_id()
    {
        var viaPendingId = Existing(Jan5, -500, "COFFEE", providerId: null, pendingId: "PEND1", n: 1);
        var viaProviderId = Existing(Jan5, -700, "BAGELS", providerId: "PEND2", n: 2);

        var results = Classify(
        [
            new(Jan5.AddDays(1), -520, "COFFEE", "POST1", "PEND1"),
            new(Jan5.AddDays(1), -700, "BAGELS", "POST2", "PEND2"),
        ], viaPendingId, viaProviderId);

        results[0].Decision.ShouldBe(DedupDecision.UpdatePendingToPosted);
        results[0].ExistingId.ShouldBe(viaPendingId.Id);
        results[0].HasChanges.ShouldBeTrue();
        results[1].Decision.ShouldBe(DedupDecision.UpdatePendingToPosted);
        results[1].ExistingId.ShouldBe(viaProviderId.Id);
    }

    [Fact]
    public void Step3_exact_fingerprint_is_skipped()
    {
        var existing = Existing(Jan5, -500, "POS DEBIT COFFEE 12345");
        var results = Classify([new(Jan5, -500, "coffee")], existing);

        results[0].Decision.ShouldBe(DedupDecision.SkipExactFingerprint);
        results[0].ExistingId.ShouldBe(existing.Id);
    }

    [Fact]
    public void Step3_uses_the_stored_fingerprint_when_present()
    {
        var fingerprint = ImportFingerprint.Compute(Account, Jan5, -500, "COFFEE");
        var edited = Existing(Jan5, -500, "Renamed by the user") with { ImportFingerprint = fingerprint };

        Classify([new(Jan5, -500, "COFFEE")], edited)[0].Decision.ShouldBe(DedupDecision.SkipExactFingerprint);
    }

    [Fact]
    public void Step3_matches_each_existing_row_once()
    {
        var results = Classify(
            [new(Jan5, -500, "COFFEE"), new(Jan5, -500, "COFFEE")],
            Existing(Jan5, -500, "COFFEE"));

        results.Select(r => r.Decision).ShouldBe([DedupDecision.SkipExactFingerprint, DedupDecision.Insert]);
    }

    [Fact]
    public void Step3_ignores_other_accounts()
    {
        Classify([new(Jan5, -500, "COFFEE")], Existing(Jan5, -500, "COFFEE", account: OtherAccount))[0]
            .Decision.ShouldBe(DedupDecision.Insert);
    }

    [Fact]
    public void Step4_fuzzy_matches_a_manual_row_within_two_days()
    {
        var manual = Existing(Jan5, -1250, "Starbucks", TransactionSource.Manual);
        var result = Classify([new(Jan5.AddDays(2), -1250, "SQ *STARBUCKS STORE 1234")], manual)[0];

        result.Decision.ShouldBe(DedupDecision.MatchExistingManual);
        result.ExistingId.ShouldBe(manual.Id);
        result.PayeeSimilarity!.Value.ShouldBeGreaterThanOrEqualTo(0.85);
    }

    [Fact]
    public void Step4_matches_scheduled_rows_too()
    {
        var scheduled = Existing(Jan5, -150000, "Landlord LLC", TransactionSource.Scheduled);
        Classify([new(Jan5.AddDays(-1), -150000, "LANDLORD LLC")], scheduled)[0]
            .Decision.ShouldBe(DedupDecision.MatchExistingManual);
    }

    [Theory]
    [InlineData(3, -1250, "Starbucks", TransactionSource.Manual, false)] // too far apart
    [InlineData(1, -1251, "Starbucks", TransactionSource.Manual, false)] // amount differs
    [InlineData(1, -1250, "Shell Oil", TransactionSource.Manual, false)] // payee differs
    [InlineData(1, -1250, "Starbucks", TransactionSource.File, false)]   // not user-entered
    [InlineData(1, -1250, "Starbucks", TransactionSource.Provider, false)]
    [InlineData(1, -1250, "Starbucks", TransactionSource.Manual, true)]  // already matched to an import
    public void Step4_requires_every_condition(int days, long amount, string payee, TransactionSource source, bool hasImportMatch)
    {
        var existing = Existing(Jan5, amount, payee, source, hasImportMatch: hasImportMatch);
        Classify([new(Jan5.AddDays(days), -1250, "STARBUCKS SEATTLE")], existing)[0]
            .Decision.ShouldBe(DedupDecision.Insert);
    }

    [Fact]
    public void Step4_ignores_manual_rows_that_already_carry_a_provider_id()
    {
        var linked = Existing(Jan5, -1250, "Starbucks", TransactionSource.Manual, providerId: "FIT9");
        Classify([new(Jan5, -1250, "STARBUCKS SEATTLE")], linked)[0].Decision.ShouldBe(DedupDecision.Insert);
    }

    [Fact]
    public void Step4_prefers_the_most_similar_then_the_closest_row()
    {
        var far = Existing(Jan5.AddDays(-2), -1250, "Starbucks Seattle", TransactionSource.Manual, n: 1);
        var near = Existing(Jan5.AddDays(-1), -1250, "Starbucks Seattle", TransactionSource.Manual, n: 2);
        var lessSimilar = Existing(Jan5, -1250, "Starbucks Coffee Co", TransactionSource.Manual, n: 3);

        Classify([new(Jan5, -1250, "STARBUCKS SEATTLE")], far, near, lessSimilar)[0].ExistingId.ShouldBe(near.Id);
    }

    [Fact]
    public void Exact_matches_win_over_an_earlier_rows_fuzzy_match()
    {
        // Row 0 would fuzzy-match the manual row, but row 1 is its exact duplicate.
        var manual = Existing(Jan5, -1250, "STARBUCKS", TransactionSource.Manual);
        var results = Classify([new(Jan5.AddDays(1), -1250, "STARBUCKS"), new(Jan5, -1250, "STARBUCKS")], manual);

        results[0].Decision.ShouldBe(DedupDecision.Insert);
        results[1].Decision.ShouldBe(DedupDecision.SkipExactFingerprint);
        results[1].ExistingId.ShouldBe(manual.Id);
    }

    [Fact]
    public void Provider_id_match_takes_precedence_over_fingerprint()
    {
        var byId = Existing(Jan5.AddDays(-10), -999, "OLD", providerId: "FIT1", n: 1);
        var byFingerprint = Existing(Jan5, -500, "COFFEE", n: 2);

        var result = Classify([new(Jan5, -500, "COFFEE", "FIT1")], byId, byFingerprint)[0];
        result.Decision.ShouldBe(DedupDecision.UpdateByProviderId);
        result.ExistingId.ShouldBe(byId.Id);
    }

    [Fact]
    public void Custom_options_widen_the_fuzzy_window()
    {
        var manual = Existing(Jan5, -1250, "Starbucks", TransactionSource.Manual);
        DuplicateMatcher.Classify(Account, [new ImportRow(Jan5.AddDays(4), -1250, "STARBUCKS")], [manual],
            options: new DedupOptions(FuzzyDayWindow: 5))[0].Decision.ShouldBe(DedupDecision.MatchExistingManual);
    }

    [Fact]
    public void Result_order_and_indexes_follow_the_batch()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new ImportRow(Jan5.AddDays(i), -100 * (i + 1), $"PAYEE {i}")).ToList();
        var results = Classify(rows);
        results.Select(r => r.Index).ShouldBe(Enumerable.Range(0, 20));
    }
}
