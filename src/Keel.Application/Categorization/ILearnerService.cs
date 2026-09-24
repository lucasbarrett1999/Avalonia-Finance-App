using Keel.Domain.Categorization;

namespace Keel.Application.Categorization;

/// <summary>
/// The learner model of the open budget file (F-TXN-5, ADR 0021): built from approved history on
/// first use, cached as JSON in the file's <c>Setting</c> table, kept current incrementally
/// (<see cref="LearnerModel.WithExample"/>/<see cref="LearnerModel.WithoutExample"/>) as
/// approvals and categories change, and rebuilt when the normalizer or model format version
/// changes. All work runs on the thread pool.
/// </summary>
public interface ILearnerService
{
    /// <summary>Current state.</summary>
    LearnerStatus Status { get; }

    /// <summary>Raised (on any thread) when <see cref="Status"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>The current model: loads the cache or builds it the first time, then catches up with ledger changes.</summary>
    Task<LearnerModel> GetModelAsync(CancellationToken ct);

    /// <summary>Starts loading in the background (the review screen's "preparing suggestions" state) and completes when ready.</summary>
    Task WarmUpAsync(CancellationToken ct);

    /// <summary>Discards the cache and retrains from history.</summary>
    Task<LearnerModel> RebuildAsync(CancellationToken ct);
}

/// <summary>Learner state for the UI.</summary>
public enum LearnerStatus
{
    /// <summary>Nothing loaded yet.</summary>
    NotLoaded,

    /// <summary>Loading the cache or training from history.</summary>
    Preparing,

    /// <summary>Ready.</summary>
    Ready,

    /// <summary>Loading failed; suggestions come from rules and payee defaults only.</summary>
    Failed,
}
