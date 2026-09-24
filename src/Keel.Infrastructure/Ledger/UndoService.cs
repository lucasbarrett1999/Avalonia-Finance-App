using Keel.Application.Undo;

namespace Keel.Infrastructure.Ledger;

/// <summary>
/// Replays the inverse (undo) or the original (redo) of recorded row changes in one audited unit
/// of work. The inverse of a creation is a delete, of a deletion an insert, and of an update an
/// update back to the recorded "before" values.
/// </summary>
public sealed class UndoService : IUndoService
{
    private readonly LedgerWriter _writer;
    private readonly UndoHistory _history;

    /// <summary>Creates the service.</summary>
    public UndoService(LedgerWriter writer, UndoHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _writer = writer;
        _history = history;
        _history.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool CanUndo => _history.UndoCount > 0;

    /// <inheritdoc />
    public bool CanRedo => _history.RedoCount > 0;

    /// <inheritdoc />
    public LedgerAction? NextUndo => _history.PeekUndo();

    /// <inheritdoc />
    public LedgerAction? NextRedo => _history.PeekRedo();

    /// <inheritdoc />
    public Task<LedgerAction?> UndoAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            if (_history.PopUndo() is not { } entry)
            {
                return (LedgerAction?)null;
            }

            try
            {
                await ReplayAsync(entry, undo: true, ct).ConfigureAwait(false);
            }
            catch
            {
                _history.PushUndo(entry);
                throw;
            }

            _history.PushRedo(entry);
            return entry.Action;
        },
        ct);

    /// <inheritdoc />
    public Task<LedgerAction?> RedoAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            if (_history.PopRedo() is not { } entry)
            {
                return (LedgerAction?)null;
            }

            try
            {
                await ReplayAsync(entry, undo: false, ct).ConfigureAwait(false);
            }
            catch
            {
                _history.PushRedo(entry);
                throw;
            }

            _history.PushUndo(entry);
            return entry.Action;
        },
        ct);

    /// <inheritdoc />
    public void Clear() => _history.Clear();

    private Task<bool> ReplayAsync(UndoEntry entry, bool undo, CancellationToken ct) =>
        _writer.RunCoreAsync(
            entry.Action,
            session =>
            {
                var changes = undo ? entry.Changes.Reverse() : entry.Changes;
                foreach (var change in changes)
                {
                    var current = undo ? change.After : change.Before;
                    var target = undo ? change.Before : change.After;
                    EntityChange.Stage(session.Db, change.EntityType, current, target);
                }

                return Task.FromResult(true);
            },
            LedgerWriter.Recording.None,
            ct);
}
