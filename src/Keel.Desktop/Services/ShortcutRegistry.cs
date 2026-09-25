using Avalonia.Input;
using Keel.Desktop.Resources;

namespace Keel.Desktop.Services;

/// <summary>Where a shortcut works (the Settings → Keyboard shortcuts headings).</summary>
public enum ShortcutScope
{
    /// <summary>Anywhere in the main window.</summary>
    General,

    /// <summary>Account registers.</summary>
    Register,

    /// <summary>The Budget screen.</summary>
    Budget,

    /// <summary>The Review queue.</summary>
    Review,

    /// <summary>The Bills screen.</summary>
    Bills,

    /// <summary>The Reports screen.</summary>
    Reports,

    /// <summary>The Goals screen.</summary>
    Goals,

    /// <summary>Bank connections (Settings → Connections, the top bar and linked registers).</summary>
    Connections,

    /// <summary>Dialogs and editors.</summary>
    Dialogs,
}

/// <summary>A registered shortcut.</summary>
/// <param name="Id">Stable id (palette and menu entries show the keys of the entry with their id).</param>
/// <param name="Scope">Where it works.</param>
/// <param name="Action">What it does (from Strings.resx).</param>
/// <param name="Keys">Platform-specific key text.</param>
/// <param name="Gesture">The gesture, when it is a single key chord.</param>
public sealed record ShortcutEntry(string Id, ShortcutScope Scope, string Action, string Keys, KeyGesture? Gesture = null);

/// <summary>
/// The keyboard shortcut registry (F-SET-5): every shortcut of every screen in one list. Settings →
/// Keyboard shortcuts, the command palette and the menus are generated from it, and a test checks that
/// the window's key bindings and each screen's handlers match it.
/// </summary>
public sealed class ShortcutRegistry
{
    /// <summary>Builds the registry for a platform.</summary>
    public ShortcutRegistry(PlatformShortcuts shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        Platform = shortcuts;
        var list = new List<ShortcutEntry>();
        void Add(string id, ShortcutScope scope, string action, KeyGesture gesture) => list.Add(new(id, scope, action, shortcuts.Format(gesture), gesture));
        void AddText(string id, ShortcutScope scope, string action, string keys) => list.Add(new(id, scope, action, keys));
        var cmd = shortcuts.CommandModifiers;

        Add("palette", ShortcutScope.General, Strings.Shortcut_CommandPalette, shortcuts.CommandPalette);
        Add("search", ShortcutScope.General, Strings.Shortcut_Search, shortcuts.Search);
        Add("undo", ShortcutScope.General, Strings.Shortcut_Undo, shortcuts.Undo);
        Add("redo", ShortcutScope.General, Strings.Shortcut_Redo, shortcuts.Redo);
        if (!shortcuts.RedoAlternate.Equals(shortcuts.Redo))
        {
            Add("redo-alt", ShortcutScope.General, Strings.Shortcut_RedoAlternate, shortcuts.RedoAlternate);
        }
        Add("sidebar", ShortcutScope.General, Strings.Shortcut_ToggleSidebar, shortcuts.ToggleSidebar);
        Add("settings", ShortcutScope.General, Strings.Shortcut_Settings, shortcuts.Settings);
        Add("open-file", ShortcutScope.General, Strings.Shortcut_OpenFile, shortcuts.OpenFile);
        string[] pages = [Strings.Nav_Home, Strings.Nav_Budget, Strings.Nav_Review, Strings.Nav_Bills, Strings.Nav_Goals, Strings.Nav_Reports, Strings.Nav_AllAccounts];
        for (var i = 0; i < pages.Length; i++)
        {
            Add("go-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ShortcutScope.General, LedgerTextFormat(Strings.Shortcut_GoTo, pages[i]), shortcuts.GoTo(i));
        }

        if (shortcuts.Quit is { } quit)
        {
            Add("quit", ShortcutScope.General, Strings.Shortcut_Quit, quit);
        }

        Add("register-new", ShortcutScope.Register, Strings.Shortcut_NewTransaction, new KeyGesture(Key.N));
        Add("register-edit", ShortcutScope.Register, Strings.Shortcut_EditTransaction, new KeyGesture(Key.Enter));
        Add("register-cleared", ShortcutScope.Register, Strings.Shortcut_ToggleCleared, new KeyGesture(Key.C));
        Add("register-approve", ShortcutScope.Register, Strings.Shortcut_Approve, new KeyGesture(Key.A));
        Add("register-delete", ShortcutScope.Register, Strings.Shortcut_Delete, new KeyGesture(Key.Delete));
        Add("register-save-new", ShortcutScope.Register, Strings.Shortcut_SaveAndNew, new KeyGesture(Key.Enter, cmd));
        Add("register-cancel", ShortcutScope.Register, Strings.Shortcut_Cancel, new KeyGesture(Key.Escape));

        Add("budget-previous", ShortcutScope.Budget, Strings.Shortcut_BudgetPreviousMonth, shortcuts.PreviousMonth);
        Add("budget-next", ShortcutScope.Budget, Strings.Shortcut_BudgetNextMonth, shortcuts.NextMonth);
        AddText("budget-navigate", ShortcutScope.Budget, Strings.Shortcut_BudgetNavigate, "↑ ↓ ← →");
        Add("budget-edit", ShortcutScope.Budget, Strings.Shortcut_BudgetEdit, new KeyGesture(Key.Enter));
        Add("budget-next-assigned", ShortcutScope.Budget, Strings.Shortcut_BudgetNextAssigned, new KeyGesture(Key.Tab));
        Add("budget-move", ShortcutScope.Budget, Strings.Shortcut_BudgetMoveMoney, shortcuts.MoveMoney);
        Add("budget-target", ShortcutScope.Budget, Strings.Shortcut_BudgetSetTarget, shortcuts.SetTarget);
        Add("budget-fund", ShortcutScope.Budget, Strings.Shortcut_BudgetFundTargets, shortcuts.FundTargets);
        Add("budget-inspector", ShortcutScope.Budget, Strings.Shortcut_BudgetInspector, shortcuts.ToggleInspector);
        Add("budget-quick-assign", ShortcutScope.Budget, Strings.Shortcut_BudgetQuickAssign, shortcuts.QuickAssign);
        Add("budget-three-months", ShortcutScope.Budget, Strings.BudgetMonths_Shortcut, shortcuts.ToggleThreeMonths);
        Add("budget-flex", ShortcutScope.Budget, Strings.Flex_Shortcut, shortcuts.ToggleFlexView);

        Add("review-approve", ShortcutScope.Review, Strings.Shortcut_ReviewApprove, new KeyGesture(Key.A));
        AddText("review-pick", ShortcutScope.Review, Strings.Shortcut_ReviewPick, "1–9");
        Add("review-category", ShortcutScope.Review, Strings.Shortcut_ReviewCategory, new KeyGesture(Key.C));
        Add("review-split", ShortcutScope.Review, Strings.Shortcut_ReviewSplit, new KeyGesture(Key.S));
        Add("review-transfer", ShortcutScope.Review, Strings.Shortcut_ReviewTransfer, new KeyGesture(Key.T));
        Add("review-rule", ShortcutScope.Review, Strings.Shortcut_ReviewRule, new KeyGesture(Key.R));
        Add("review-delete", ShortcutScope.Review, Strings.Shortcut_ReviewDelete, new KeyGesture(Key.D));
        AddText("review-move", ShortcutScope.Review, Strings.Shortcut_ReviewMove, "J / K");

        AddText("bills-tabs", ShortcutScope.Bills, Strings.Shortcut_BillsTabs, "1 / 2 / 3");
        Add("bills-new", ShortcutScope.Bills, Strings.Shortcut_BillsNew, new KeyGesture(Key.N));
        Add("bills-detect", ShortcutScope.Bills, Strings.Shortcut_BillsDetect, new KeyGesture(Key.R));
        Add("bills-close", ShortcutScope.Bills, Strings.Shortcut_BillsClose, new KeyGesture(Key.Escape));

        AddText("reports-pick", ShortcutScope.Reports, Strings.Shortcut_ReportsPick, "1–5");
        Add("reports-export", ShortcutScope.Reports, Strings.Shortcut_ReportsExport, new KeyGesture(Key.E, cmd));

        Add("goals-new", ShortcutScope.Goals, Strings.Shortcut_GoalsNew, new KeyGesture(Key.N));

        Add("sync-all", ShortcutScope.Connections, Strings.Shortcut_SyncAll, shortcuts.SyncAll);

        Add("dialog-confirm", ShortcutScope.Dialogs, Strings.Shortcut_DialogConfirm, new KeyGesture(Key.Enter));
        Add("dialog-cancel", ShortcutScope.Dialogs, Strings.Shortcut_DialogCancel, new KeyGesture(Key.Escape));
        AddText("money-math", ShortcutScope.Dialogs, Strings.Shortcut_MoneyMath, "+ − × ÷");

        // M9b: PNG export of report charts and the Goals tabs.
        Add("reports-export-png", ShortcutScope.Reports, Strings.ExportPng_Shortcut, new KeyGesture(Key.E, cmd | KeyModifiers.Shift));
        AddText("goals-tabs", ShortcutScope.Goals, Strings.Debt_Shortcut_Tabs, "1 / 2");

        // Entries added later append above; the reference lists them grouped by scope (a stable sort).
        All = [.. list.OrderBy(e => e.Scope)];
    }

    /// <summary>The platform the keys are formatted for.</summary>
    public PlatformShortcuts Platform { get; }

    /// <summary>Every shortcut, grouped by scope in display order.</summary>
    public IReadOnlyList<ShortcutEntry> All { get; }

    /// <summary>Heading of a scope.</summary>
    public static string ScopeTitle(ShortcutScope scope) => Strings.ResourceManager.GetString("ShortcutScope_" + scope, Strings.Culture) ?? scope.ToString();

    /// <summary>The entry with <paramref name="id"/>, or null.</summary>
    public ShortcutEntry? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    /// <summary>Key text of the entry with <paramref name="id"/>, or null.</summary>
    public string? KeysFor(string id) => Find(id)?.Keys;

    private static string LedgerTextFormat(string template, string arg) => string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arg);
}
