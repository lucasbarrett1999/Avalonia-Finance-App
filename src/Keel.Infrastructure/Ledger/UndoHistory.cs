using Keel.Application.Undo;

namespace Keel.Infrastructure.Ledger;

/// <summary>One undoable user action: its kind and the coalesced row changes it made.</summary>
internal sealed record UndoEntry(LedgerAction Action, IReadOnlyList<EntityChange> Changes);

/// <summary>The session undo and redo stacks (thread-safe), capped at <see cref="IUndoService.Capacity"/>.</summary>
public sealed class UndoHistory
{
    private readonly Lock _gate = new();
    private readonly LinkedList<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();

    /// <summary>Raised after either stack changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Number of undoable actions.</summary>
    public int UndoCount
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count;
            }
        }
    }

    /// <summary>Number of redoable actions.</summary>
    public int RedoCount
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count;
            }
        }
    }

    /// <summary>The action an undo would revert.</summary>
    public LedgerAction? PeekUndo()
    {
        lock (_gate)
        {
            return _undo.Last?.Value.Action;
        }
    }

    /// <summary>The action a redo would re-apply.</summary>
    public LedgerAction? PeekRedo()
    {
        lock (_gate)
        {
            return _redo.TryPeek(out var entry) ? entry.Action : null;
        }
    }

    /// <summary>Forgets both stacks.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _undo.Clear();
            _redo.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a new action; a new action clears the redo stack.</summary>
    internal void Record(UndoEntry entry)
    {
        lock (_gate)
        {
            _undo.AddLast(entry);
            while (_undo.Count > IUndoService.Capacity)
            {
                _undo.RemoveFirst();
            }

            _redo.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal UndoEntry? PopUndo()
    {
        lock (_gate)
        {
            if (_undo.Last is not { } last)
            {
                return null;
            }

            _undo.RemoveLast();
            return last.Value;
        }
    }

    internal UndoEntry? PopRedo()
    {
        lock (_gate)
        {
            return _redo.TryPop(out var entry) ? entry : null;
        }
    }

    internal void PushUndo(UndoEntry entry, bool notify = true)
    {
        lock (_gate)
        {
            _undo.AddLast(entry);
            while (_undo.Count > IUndoService.Capacity)
            {
                _undo.RemoveFirst();
            }
        }

        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void PushRedo(UndoEntry entry, bool notify = true)
    {
        lock (_gate)
        {
            _redo.Push(entry);
        }

        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
