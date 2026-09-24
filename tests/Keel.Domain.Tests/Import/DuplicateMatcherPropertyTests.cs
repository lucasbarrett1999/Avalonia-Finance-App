using CsCheck;
using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

/// <summary>
/// PRD 13 / F-TXN-2: importing the same batch twice adds nothing the second time. The test runs
/// the dedup classification, applies the outcome the way the import pipeline stores it, then
/// classifies the identical batch again and requires zero inserts.
/// </summary>
public class DuplicateMatcherPropertyTests
{
    private static readonly Guid Account = Guid.Parse("0190a1b2-0000-7000-8000-000000000001");
    private static readonly DateOnly Start = new(2026, 1, 1);

    private static readonly string[] Payees =
    [
        "SQ *BLUE BOTTLE", "Blue Bottle", "STARBUCKS #1234", "Starbucks", "AMZN Mktp US*2K4AB1CD2",
        "Amazon", "SHELL OIL 57444212500", "Shell", "TRADER JOE'S #552", "Landlord LLC", "", "POS DEBIT 99999",
    ];

    private static readonly Gen<ImportRow> GenRow =
        Gen.Select(
            Gen.Int[0, 20],
            Gen.OneOfConst(-1250L, -500L, -150000L, 2500L, -1L, 0L),
            Gen.OneOfConst(Payees),
            Gen.Int[0, 8].Select(i => i < 3 ? $"FIT{i}" : null),
            Gen.Int[0, 8].Select(i => i < 2 ? $"FIT{i}" : null))
        .Select((day, amount, payee, id, pending) => new ImportRow(Start.AddDays(day), amount, payee, id, pending));

    private static readonly Gen<DedupCandidate> GenManual =
        Gen.Select(Gen.Int[0, 20], Gen.OneOfConst(-1250L, -500L, -150000L, 2500L), Gen.OneOfConst(Payees), Gen.Guid, Gen.Bool)
        .Select((day, amount, payee, id, scheduled) => new DedupCandidate(
            id, Account, Start.AddDays(day), amount, PayeeNormalizer.Normalize(payee),
            scheduled ? TransactionSource.Scheduled : TransactionSource.Manual));

    [Fact]
    public void Importing_the_same_batch_twice_inserts_nothing_the_second_time()
    {
        Gen.Select(GenRow.List[0, 40], GenManual.List[0, 10])
            .Sample((batch, manual) =>
            {
                var first = DuplicateMatcher.Classify(Account, batch, manual);
                var stored = Apply(manual, batch, first);
                var second = DuplicateMatcher.Classify(Account, batch, stored);
                return second.All(r => r.Decision != DedupDecision.Insert);
            }, iter: 2_000);
    }

    [Fact]
    public void Classification_is_deterministic()
    {
        Gen.Select(GenRow.List[0, 30], GenManual.List[0, 10])
            .Sample((batch, manual) =>
            {
                var a = DuplicateMatcher.Classify(Account, batch, manual);
                var b = DuplicateMatcher.Classify(Account, batch, manual.AsEnumerable().Reverse().ToList());
                return a.SequenceEqual(b);
            }, iter: 500);
    }

    /// <summary>
    /// Reference model of how the pipeline persists each decision: inserts become File rows with
    /// the row's fingerprint and provider id; matches attach the fingerprint and provider id;
    /// updates rewrite date, amount and payee.
    /// </summary>
    private static List<DedupCandidate> Apply(List<DedupCandidate> existing, List<ImportRow> batch, IReadOnlyList<DedupResult> results)
    {
        var rows = existing.ToDictionary(c => c.Id);
        var inserted = new List<DedupCandidate>();
        var n = 0;
        foreach (var r in results)
        {
            var row = batch[r.Index];
            switch (r.Decision)
            {
                case DedupDecision.Insert:
                    inserted.Add(new DedupCandidate(
                        new Guid(n++, 0, 0, [1, 2, 3, 4, 5, 6, 7, 8]), Account, row.Date, row.Amount, r.NormalizedPayee,
                        TransactionSource.File, row.ProviderTransactionId, null, r.Fingerprint));
                    break;
                case DedupDecision.MatchExistingManual:
                    rows[r.ExistingId!.Value] = rows[r.ExistingId.Value] with
                    {
                        ProviderTransactionId = row.ProviderTransactionId,
                        ImportFingerprint = r.Fingerprint,
                        HasImportMatch = true,
                    };
                    break;
                case DedupDecision.UpdateByProviderId:
                case DedupDecision.UpdatePendingToPosted:
                    rows[r.ExistingId!.Value] = rows[r.ExistingId.Value] with
                    {
                        Date = row.Date,
                        Amount = row.Amount,
                        NormalizedPayee = r.NormalizedPayee,
                        ProviderTransactionId = row.ProviderTransactionId ?? rows[r.ExistingId.Value].ProviderTransactionId,
                        ImportFingerprint = r.Fingerprint,
                    };
                    break;
                case DedupDecision.SkipExactFingerprint:
                    break;
                default:
                    throw new InvalidOperationException(r.Decision.ToString());
            }
        }

        return [.. rows.Values, .. inserted];
    }
}
