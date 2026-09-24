namespace Keel.Domain.Import;

/// <summary>What the import pipeline should do with one incoming row (PRD 6.5).</summary>
public enum DedupDecision
{
    /// <summary>Step 5: no match; insert as a new (unapproved) transaction.</summary>
    Insert,

    /// <summary>Step 1: the provider id matched an existing row; update it in place.</summary>
    UpdateByProviderId,

    /// <summary>Step 2: a posted row replaces its pending row; update in place and set the provider id.</summary>
    UpdatePendingToPosted,

    /// <summary>Step 3: the exact fingerprint already exists; skip as a duplicate.</summary>
    SkipExactFingerprint,

    /// <summary>
    /// Step 4: matched a manual or scheduled row the user entered first; attach the provider id
    /// and fingerprint to it instead of inserting. Shown as "matched to existing" in the preview.
    /// </summary>
    MatchExistingManual,
}

/// <summary>
/// An existing, non-deleted transaction that incoming rows may match. The pipeline loads these
/// for the target account (and sync connection) over the batch's date range plus a margin.
/// </summary>
/// <param name="Id">Transaction id.</param>
/// <param name="AccountId">Account of the transaction.</param>
/// <param name="Date">Ledger date.</param>
/// <param name="Amount">Signed amount in minor units.</param>
/// <param name="NormalizedPayee"><see cref="PayeeNormalizer.Normalize"/> of its raw payee.</param>
/// <param name="Source">Where the row came from; only Manual and Scheduled rows are fuzzy-matched.</param>
/// <param name="ProviderTransactionId">Stored provider id (FITID, Plaid id), if any.</param>
/// <param name="ProviderPendingId">Provider id of the pending row this transaction was created from, if any.</param>
/// <param name="ImportFingerprint">Stored fingerprint; when null it is computed from the other fields.</param>
/// <param name="SyncConnectionId">Connection that produced it, for provider-id scoping.</param>
/// <param name="HasImportMatch">True when an imported row was already matched to this manual row;
/// such rows are not fuzzy-matched again.</param>
public sealed record DedupCandidate(
    Guid Id,
    Guid AccountId,
    DateOnly Date,
    long Amount,
    string NormalizedPayee,
    TransactionSource Source,
    string? ProviderTransactionId = null,
    string? ProviderPendingId = null,
    string? ImportFingerprint = null,
    Guid? SyncConnectionId = null,
    bool HasImportMatch = false);

/// <summary>The classification of one incoming row.</summary>
/// <param name="Index">Position of the row in the incoming batch.</param>
/// <param name="Decision">What to do with it.</param>
/// <param name="ExistingId">The matched existing transaction (null for <see cref="DedupDecision.Insert"/>).</param>
/// <param name="NormalizedPayee">Normalized payee of the incoming row.</param>
/// <param name="Fingerprint">Fingerprint of the incoming row, to store on insert or attach on match.</param>
/// <param name="PayeeSimilarity">Jaro-Winkler similarity for fuzzy matches.</param>
/// <param name="HasChanges">For updates: whether date, amount or payee differ from the stored row.</param>
public sealed record DedupResult(
    int Index,
    DedupDecision Decision,
    Guid? ExistingId,
    string NormalizedPayee,
    string Fingerprint,
    double? PayeeSimilarity = null,
    bool HasChanges = false);

/// <summary>Tuning knobs for <see cref="DuplicateMatcher"/>; defaults are PRD 6.5.</summary>
/// <param name="FuzzyDayWindow">Maximum date distance in days for a fuzzy match.</param>
/// <param name="FuzzyPayeeThreshold">Minimum Jaro-Winkler similarity for a fuzzy match.</param>
public sealed record DedupOptions(int FuzzyDayWindow = 2, double FuzzyPayeeThreshold = 0.85)
{
    /// <summary>The PRD 6.5 values.</summary>
    public static DedupOptions Default { get; } = new();
}

/// <summary>
/// Classifies incoming rows against existing transactions per PRD 6.5. Pure and deterministic:
/// the same inputs always give the same decisions.
/// </summary>
/// <remarks>
/// The steps run as passes over the whole batch, in PRD order: provider id, pending-to-posted,
/// exact fingerprint, fuzzy manual match; whatever is left is inserted. Running them as passes
/// (rather than all steps per row) means an exact match is never stolen by an earlier row's fuzzy
/// match. Each existing row is matched at most once per batch, except by provider id, which is a
/// key: two incoming rows with the same fingerprint (two identical coffees on one day) need two
/// existing rows to both be skipped (ADR 0005).
/// </remarks>
public static class DuplicateMatcher
{
    /// <summary>Classifies <paramref name="incoming"/> rows for one target account.</summary>
    /// <param name="accountId">Account the batch is imported into.</param>
    /// <param name="incoming">Incoming rows, in file or provider order.</param>
    /// <param name="existing">Existing non-deleted candidates (other accounts are ignored).</param>
    /// <param name="syncConnectionId">When set, provider ids are matched within this connection
    /// instead of within the account (PRD 6.5 step 1).</param>
    /// <param name="options">Fuzzy-match knobs; <see cref="DedupOptions.Default"/> when null.</param>
    /// <returns>One result per incoming row, in the same order.</returns>
    public static IReadOnlyList<DedupResult> Classify<T>(
        Guid accountId,
        IReadOnlyList<T> incoming,
        IEnumerable<DedupCandidate> existing,
        Guid? syncConnectionId = null,
        DedupOptions? options = null)
        where T : IImportRecord
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existing);
        options ??= DedupOptions.Default;

        var candidates = existing
            .OrderBy(c => c.Date)
            .ThenBy(c => c.Id)
            .ToList();
        bool InProviderScope(DedupCandidate c) =>
            syncConnectionId is { } connection ? c.SyncConnectionId == connection : c.AccountId == accountId;

        var byProviderId = new Dictionary<string, DedupCandidate>(StringComparer.Ordinal);
        var byPendingId = new Dictionary<string, DedupCandidate>(StringComparer.Ordinal);
        foreach (var c in candidates.Where(InProviderScope))
        {
            if (!string.IsNullOrEmpty(c.ProviderTransactionId))
            {
                byProviderId.TryAdd(c.ProviderTransactionId, c);
            }

            if (!string.IsNullOrEmpty(c.ProviderPendingId))
            {
                byPendingId.TryAdd(c.ProviderPendingId, c);
            }
        }

        var inAccount = candidates.Where(c => c.AccountId == accountId).ToList();
        var byFingerprint = inAccount
            .GroupBy(c => c.ImportFingerprint ?? ImportFingerprint.Compute(c.AccountId, c.Date, c.Amount, c.NormalizedPayee), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<DedupCandidate>(g), StringComparer.Ordinal);
        var byAmount = inAccount
            .Where(c => c.Source is TransactionSource.Manual or TransactionSource.Scheduled
                && c.ProviderTransactionId is null && !c.HasImportMatch)
            .GroupBy(c => c.Amount)
            .ToDictionary(g => g.Key, g => g.ToList());

        var normalized = new string[incoming.Count];
        var fingerprints = new string[incoming.Count];
        for (var i = 0; i < incoming.Count; i++)
        {
            normalized[i] = PayeeNormalizer.Normalize(incoming[i].PayeeRaw);
            fingerprints[i] = ImportFingerprint.Compute(accountId, incoming[i].Date, incoming[i].Amount, normalized[i]);
        }

        var results = new DedupResult?[incoming.Count];
        var claimed = new HashSet<Guid>();

        // Step 1: provider id. A key, so it matches even an already claimed row.
        for (var i = 0; i < incoming.Count; i++)
        {
            var id = incoming[i].ProviderTransactionId;
            if (!string.IsNullOrEmpty(id) && byProviderId.TryGetValue(id, out var match))
            {
                claimed.Add(match.Id);
                results[i] = Result(i, DedupDecision.UpdateByProviderId, match, HasChanges(incoming[i], normalized[i], match));
            }
        }

        // Step 2: pending to posted. The pending row may carry its id as ProviderPendingId or,
        // if it was inserted while pending, as its ProviderTransactionId.
        for (var i = 0; i < incoming.Count; i++)
        {
            var pendingId = incoming[i].PendingTransactionId;
            if (results[i] is not null || string.IsNullOrEmpty(pendingId))
            {
                continue;
            }

            if ((byPendingId.TryGetValue(pendingId, out var match) || byProviderId.TryGetValue(pendingId, out match))
                && claimed.Add(match.Id))
            {
                results[i] = Result(i, DedupDecision.UpdatePendingToPosted, match, HasChanges(incoming[i], normalized[i], match));
            }
        }

        // Step 3: exact fingerprint, one existing row per incoming row.
        for (var i = 0; i < incoming.Count; i++)
        {
            if (results[i] is not null || !byFingerprint.TryGetValue(fingerprints[i], out var queue))
            {
                continue;
            }

            while (queue.TryDequeue(out var match))
            {
                if (claimed.Add(match.Id))
                {
                    results[i] = Result(i, DedupDecision.SkipExactFingerprint, match, hasChanges: false);
                    break;
                }
            }
        }

        // Step 4: fuzzy match to a manual or scheduled row.
        for (var i = 0; i < incoming.Count; i++)
        {
            if (results[i] is not null || !byAmount.TryGetValue(incoming[i].Amount, out var sameAmount))
            {
                continue;
            }

            DedupCandidate? best = null;
            double bestScore = 0;
            var bestDistance = int.MaxValue;
            foreach (var c in sameAmount)
            {
                var distance = Math.Abs(c.Date.DayNumber - incoming[i].Date.DayNumber);
                if (distance > options.FuzzyDayWindow || claimed.Contains(c.Id))
                {
                    continue;
                }

                var score = JaroWinkler.Similarity(normalized[i], c.NormalizedPayee);
                if (score < options.FuzzyPayeeThreshold)
                {
                    continue;
                }

                // Candidates are sorted by (date, id), so strict comparisons keep ties deterministic.
                if (best is null || score > bestScore || (score == bestScore && distance < bestDistance))
                {
                    best = c;
                    bestScore = score;
                    bestDistance = distance;
                }
            }

            if (best is not null)
            {
                claimed.Add(best.Id);
                results[i] = Result(i, DedupDecision.MatchExistingManual, best, hasChanges: false) with { PayeeSimilarity = bestScore };
            }
        }

        // Step 5: insert.
        for (var i = 0; i < incoming.Count; i++)
        {
            results[i] ??= new DedupResult(i, DedupDecision.Insert, null, normalized[i], fingerprints[i]);
        }

        return results!;

        DedupResult Result(int index, DedupDecision decision, DedupCandidate match, bool hasChanges) =>
            new(index, decision, match.Id, normalized[index], fingerprints[index], null, hasChanges);
    }

    private static bool HasChanges(IImportRecord incoming, string normalizedPayee, DedupCandidate existing) =>
        incoming.Date != existing.Date
        || incoming.Amount != existing.Amount
        || !string.Equals(normalizedPayee, existing.NormalizedPayee, StringComparison.Ordinal);
}
