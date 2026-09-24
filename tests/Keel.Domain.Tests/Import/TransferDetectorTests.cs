using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

public class TransferDetectorTests
{
    private static readonly Guid Checking = Guid.Parse("0190a1b2-0000-7000-8000-0000000000a1");
    private static readonly Guid Savings = Guid.Parse("0190a1b2-0000-7000-8000-0000000000a2");
    private static readonly Guid Card = Guid.Parse("0190a1b2-0000-7000-8000-0000000000a3");
    private static readonly DateOnly Jan5 = new(2026, 1, 5);

    private static TransferCandidate C(int n, Guid account, int day, long amount, bool incoming = true) =>
        new(Guid.Parse($"0190a1b2-0000-7000-9000-{n:D12}"), account, Jan5.AddDays(day), amount, incoming);

    [Fact]
    public void Pairs_opposite_amounts_in_different_accounts()
    {
        var outflow = C(1, Checking, 0, -50000);
        var inflow = C(2, Savings, 1, 50000);

        var match = TransferDetector.Detect([outflow, inflow]).ShouldHaveSingleItem();
        match.ShouldBe(new TransferMatch(outflow.Id, inflow.Id, 1, IsAmbiguous: false));
    }

    [Fact]
    public void Pairs_an_incoming_row_with_an_existing_row()
    {
        var existing = C(1, Card, 0, 120000, incoming: false);
        var payment = C(2, Checking, -3, -120000);
        TransferDetector.Detect([existing, payment]).ShouldHaveSingleItem().DayDifference.ShouldBe(3);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-4)]
    public void Ignores_pairs_more_than_three_days_apart(int day)
    {
        TransferDetector.Detect([C(1, Checking, 0, -50000), C(2, Savings, day, 50000)]).ShouldBeEmpty();
    }

    [Fact]
    public void Ignores_same_account_same_sign_zero_and_existing_only_pairs()
    {
        TransferDetector.Detect([C(1, Checking, 0, -50000), C(2, Checking, 0, 50000)]).ShouldBeEmpty();
        TransferDetector.Detect([C(1, Checking, 0, 50000), C(2, Savings, 0, 50000)]).ShouldBeEmpty();
        TransferDetector.Detect([C(1, Checking, 0, 0), C(2, Savings, 0, 0)]).ShouldBeEmpty();
        TransferDetector.Detect([C(1, Checking, 0, -50000, false), C(2, Savings, 0, 50000, false)]).ShouldBeEmpty();
        TransferDetector.Detect([C(1, Checking, 0, -50000), C(2, Savings, 0, 50001)]).ShouldBeEmpty();
    }

    [Fact]
    public void Uses_each_transaction_once_and_prefers_the_closest_date()
    {
        var outflow = C(1, Checking, 0, -50000);
        var far = C(2, Savings, 2, 50000);
        var near = C(3, Card, -1, 50000);
        var match = TransferDetector.Detect([outflow, far, near]).ShouldHaveSingleItem();
        match.InflowId.ShouldBe(near.Id);
        match.IsAmbiguous.ShouldBeFalse();
    }

    [Fact]
    public void Flags_equally_close_partners_as_ambiguous()
    {
        var outflow = C(1, Checking, 0, -50000);
        var a = C(2, Savings, 1, 50000);
        var b = C(3, Card, 1, 50000);
        var match = TransferDetector.Detect([outflow, a, b]).ShouldHaveSingleItem();
        match.InflowId.ShouldBe(a.Id); // lower id wins the tie
        match.IsAmbiguous.ShouldBeTrue();
    }

    [Fact]
    public void Pairs_several_transfers_in_one_batch()
    {
        var matches = TransferDetector.Detect(
        [
            C(1, Checking, 0, -50000), C(2, Savings, 0, 50000),
            C(3, Checking, 10, -50000), C(4, Savings, 11, 50000),
            C(5, Checking, 3, -2500), C(6, Card, 3, 2500),
        ]);
        matches.Count.ShouldBe(3);
        matches.Select(m => (m.OutflowId, m.InflowId)).ShouldBe(
        [
            (C(1, Checking, 0, 0).Id, C(2, Savings, 0, 0).Id),
            (C(5, Checking, 0, 0).Id, C(6, Card, 0, 0).Id),
            (C(3, Checking, 0, 0).Id, C(4, Savings, 0, 0).Id),
        ], ignoreOrder: true);
    }

    [Fact]
    public void Result_does_not_depend_on_input_order()
    {
        TransferCandidate[] input =
        [
            C(1, Checking, 0, -50000), C(2, Savings, 1, 50000), C(3, Card, 1, 50000),
            C(4, Savings, 0, -50000), C(5, Checking, 2, 50000),
        ];
        var forward = TransferDetector.Detect(input);
        var backward = TransferDetector.Detect(input.Reverse());
        forward.ShouldBe(backward);
    }
}
