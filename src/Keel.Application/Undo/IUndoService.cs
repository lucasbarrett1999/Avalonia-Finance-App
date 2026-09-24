namespace Keel.Application.Undo;

/// <summary>
/// Session undo/redo over the ledger (PRD 7.3): the last 50 actions, each replayed as the inverse
/// of its recorded before/after state. Undo and redo are themselves audited.
/// </summary>
public interface IUndoService
{
    /// <summary>Maximum number of actions kept.</summary>
    const int Capacity = 50;

    /// <summary>Whether an action can be undone.</summary>
    bool CanUndo { get; }

    /// <summary>Whether an undone action can be redone.</summary>
    bool CanRedo { get; }

    /// <summary>The action <see cref="UndoAsync"/> would undo.</summary>
    LedgerAction? NextUndo { get; }

    /// <summary>The action <see cref="RedoAsync"/> would redo.</summary>
    LedgerAction? NextRedo { get; }

    /// <summary>Raised (on any thread) when the stacks change.</summary>
    event EventHandler? Changed;

    /// <summary>Undoes the most recent action; returns it, or null when there was none.</summary>
    Task<LedgerAction?> UndoAsync(CancellationToken ct);

    /// <summary>Redoes the most recently undone action; returns it, or null when there was none.</summary>
    Task<LedgerAction?> RedoAsync(CancellationToken ct);

    /// <summary>Forgets both stacks (e.g. when another budget file is opened).</summary>
    void Clear();
}

/// <summary>User-level ledger actions; the UI shows them in undo tooltips and toasts.</summary>
public enum LedgerAction
{
    /// <summary>Add account.</summary>
    CreateAccount,

    /// <summary>Edit account.</summary>
    UpdateAccount,

    /// <summary>Close account.</summary>
    CloseAccount,

    /// <summary>Reopen account.</summary>
    ReopenAccount,

    /// <summary>Reorder accounts.</summary>
    ReorderAccounts,

    /// <summary>Add transaction.</summary>
    AddTransaction,

    /// <summary>Edit transaction.</summary>
    EditTransaction,

    /// <summary>Delete transactions.</summary>
    DeleteTransactions,

    /// <summary>Restore transactions.</summary>
    RestoreTransactions,

    /// <summary>Permanently remove deleted transactions.</summary>
    PurgeTransactions,

    /// <summary>Change cleared status.</summary>
    ChangeCleared,

    /// <summary>Approve transactions.</summary>
    Approve,

    /// <summary>Categorize transactions.</summary>
    Categorize,

    /// <summary>Move transactions to another account.</summary>
    MoveTransactions,

    /// <summary>Finish a reconciliation.</summary>
    Reconcile,

    /// <summary>Record a balance snapshot.</summary>
    RecordBalance,

    /// <summary>Delete a balance snapshot.</summary>
    DeleteBalance,

    /// <summary>Create a payee.</summary>
    CreatePayee,

    /// <summary>Create a category.</summary>
    CreateCategory,

    /// <summary>Assign money to a category (budget).</summary>
    AssignBudget,

    /// <summary>Move money between categories (budget).</summary>
    MoveMoney,

    /// <summary>Fund underfunded targets (budget).</summary>
    FundTargets,

    /// <summary>Set a category target.</summary>
    SetTarget,

    /// <summary>Remove a category target.</summary>
    DeleteTarget,

    /// <summary>Create a category group.</summary>
    CreateCategoryGroup,

    /// <summary>Rename a category group.</summary>
    RenameCategoryGroup,

    /// <summary>Hide or show a category group.</summary>
    HideCategoryGroup,

    /// <summary>Reorder category groups.</summary>
    ReorderCategoryGroups,

    /// <summary>Delete a category group.</summary>
    DeleteCategoryGroup,

    /// <summary>Rename a category.</summary>
    RenameCategory,

    /// <summary>Hide or show a category.</summary>
    HideCategory,

    /// <summary>Move a category within or between groups.</summary>
    MoveCategory,

    /// <summary>Delete a category (re-assigning its history).</summary>
    DeleteCategory,

    /// <summary>Edit a category note.</summary>
    EditCategoryNote,

    /// <summary>Apply a starter category template.</summary>
    ApplyCategoryTemplate,

    /// <summary>Edit a month note.</summary>
    EditMonthNote,
}
