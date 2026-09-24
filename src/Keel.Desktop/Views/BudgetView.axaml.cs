using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views;

/// <summary>
/// The budget view. Code-behind wires what XAML cannot: the PRD 9.3 keyboard map (cell cursor,
/// edit, Tab/Enter, M, T, I, Q, month and fund-target shortcuts), clicks on cells, focus moves
/// between the grid and the Assigned editor, and dragging an Available pill onto another row.
/// </summary>
public partial class BudgetView : UserControl
{
    /// <summary>Drag-and-drop format carrying the source category id ("" for Ready to Assign).</summary>
    internal static readonly DataFormat<string> CategoryFormat = DataFormat.CreateStringApplicationFormat("keel-budget-category");

    private const double DragThreshold = 4;
    private BudgetViewModel? _vm;
    private Point? _pressPoint;
    private Guid? _pressSource;
    private bool _pressFromReadyToAssign;
    private string? _pendingText;

    /// <summary>Creates the view.</summary>
    public BudgetView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        BudgetRows.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        BudgetRows.AddHandler(PointerMovedEvent, OnPointerMoved);
        BudgetRows.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null);
        BudgetRows.AddHandler(Button.ClickEvent, OnRowsButtonClick);
        BudgetRows.AddHandler(LostFocusEvent, OnEditorLostFocus);
        BudgetRows.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
        BudgetRows.AddHandler(DragDrop.DragLeaveEvent, OnRowDragLeave);
        BudgetRows.AddHandler(DragDrop.DropEvent, OnRowDrop);
        ReadyToAssignPill.AddHandler(PointerPressedEvent, OnPillPointerPressed, RoutingStrategies.Tunnel);
        ReadyToAssignPill.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        ReadyToAssignPill.AddHandler(DragDrop.DragOverEvent, OnReadyToAssignDragOver);
        ReadyToAssignPill.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ReadyToAssignPill.Classes.Set("dropTarget", false));
        ReadyToAssignPill.AddHandler(DragDrop.DropEvent, OnReadyToAssignDrop);
        ((PopupFlyoutBase)MonthPickerButton.Flyout!).Opening += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PickerYear = _vm.CurrentMonth.Year;
            }
        };
    }

    /// <summary>The focusable grid host (tests drive it directly).</summary>
    public Control Grid => GridHost;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.EditStarted -= OnEditStarted;
            _vm.FocusGridRequested -= OnFocusGridRequested;
            _vm.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
            _vm.Inspector.TargetFocusRequested -= OnTargetFocusRequested;
        }

        _vm = DataContext as BudgetViewModel;
        if (_vm is not null)
        {
            _vm.EditStarted += OnEditStarted;
            _vm.FocusGridRequested += OnFocusGridRequested;
            _vm.ScrollIntoViewRequested += OnScrollIntoViewRequested;
            _vm.Inspector.TargetFocusRequested += OnTargetFocusRequested;
        }
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        OnFocusGridRequested(this, EventArgs.Empty);
    }

    private IInputElement? FocusedElement => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    // PRD 9.3 keyboard map. Tunnel, so the grid sees Tab/Enter/arrows before the Assigned editor
    // and the scroll viewer do.
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled)
        {
            return;
        }

        var shortcuts = _vm.Shortcuts;
        if (shortcuts.PreviousMonth.Matches(e) || shortcuts.NextMonth.Matches(e))
        {
            CommitFocusedEditor();
            if (shortcuts.PreviousMonth.Matches(e))
            {
                _vm.PreviousMonth();
            }
            else
            {
                _vm.NextMonth();
            }

            e.Handled = true;
            return;
        }

        if (shortcuts.FundTargets.Matches(e))
        {
            CommitFocusedEditor();
            _ = _vm.FundTargetsAsync();
            e.Handled = true;
            return;
        }

        var focused = FocusedElement;
        if (focused is MoneyTextBox { Name: "AssignedEditor" } editor && editor.DataContext is BudgetCategoryRowViewModel editing)
        {
            e.Handled = HandleEditorKey(e, editor, editing);
            return;
        }

        var inTextField = focused is TextBox || (focused as Visual)?.FindAncestorOfType<TextBox>() is not null;
        if (inTextField || e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != KeyModifiers.Shift)
        {
            return;
        }

        var inGrid = ReferenceEquals(focused, GridHost) || (focused as Visual)?.FindAncestorOfType<Border>() is { Name: "GridHost" };
        e.Handled = (inGrid && HandleGridKey(e)) || HandlePageKey(e);
    }

    private bool HandleEditorKey(KeyEventArgs e, MoneyTextBox editor, BudgetCategoryRowViewModel row)
    {
        switch (e.Key)
        {
            case Key.Tab:
                editor.Commit();
                _vm!.CommitEdit(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, keepEditing: true);
                return true;
            case Key.Enter or Key.Return when e.KeyModifiers == KeyModifiers.None:
                editor.Commit();
                _vm!.CommitEdit(row, 1, keepEditing: false);
                return true;
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.None:
                editor.Commit();
                _vm!.CommitEdit(row, e.Key == Key.Down ? 1 : -1, keepEditing: false);
                return true;
            case Key.Escape:
                _vm!.CancelEdit();
                return true;
            default:
                return false;
        }
    }

    private bool HandleGridKey(KeyEventArgs e)
    {
        var vm = _vm!;
        switch (e.Key)
        {
            case Key.Up:
                vm.MoveCursor(-1, 0);
                return true;
            case Key.Down:
                vm.MoveCursor(1, 0);
                return true;
            case Key.Left:
                vm.MoveCursor(0, -1);
                return true;
            case Key.Right:
                vm.MoveCursor(0, 1);
                return true;
            case Key.Enter or Key.Return or Key.F2:
                vm.ActivateCell();
                return true;
            case Key.Space when vm.SelectedRow is BudgetGroupRowViewModel group:
                vm.ToggleGroup(group);
                return true;
            default:
                return false;
        }
    }

    // Letter shortcuts work anywhere on the page outside text fields.
    private bool HandlePageKey(KeyEventArgs e)
    {
        var vm = _vm!;
        var shortcuts = vm.Shortcuts;
        if (shortcuts.MoveMoney.Matches(e))
        {
            _ = vm.MoveMoneyAsync(null);
        }
        else if (shortcuts.SetTarget.Matches(e))
        {
            vm.SetTarget();
        }
        else if (shortcuts.ToggleInspector.Matches(e))
        {
            vm.ToggleInspector();
        }
        else if (shortcuts.QuickAssign.Matches(e))
        {
            _ = vm.QuickAssignPaletteAsync();
        }
        else
        {
            return false;
        }

        return true;
    }

    // Typing an amount on a selected Assigned cell starts editing with that text (spreadsheet style).
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_vm is null || string.IsNullOrEmpty(e.Text) || !ReferenceEquals(FocusedElement, GridHost)
            || _vm.SelectedColumn != BudgetColumn.Assigned || _vm.SelectedCategory is not { } row)
        {
            return;
        }

        var c = e.Text[0];
        if (!char.IsDigit(c) && c is not ('-' or '+' or '.' or ',' or '('))
        {
            return;
        }

        _pendingText = e.Text;
        _vm.BeginEdit(row);
        e.Handled = true;
    }

    private void CommitFocusedEditor()
    {
        if (FocusedElement is MoneyTextBox { Name: "AssignedEditor" } editor && editor.DataContext is BudgetCategoryRowViewModel row)
        {
            editor.Commit();
            _vm?.CommitEdit(row, 0, keepEditing: false);
        }
    }

    private void OnEditStarted(object? sender, BudgetCategoryRowViewModel row) => Dispatcher.UIThread.Post(
        () =>
        {
            if (!row.IsEditing || FindEditor(row) is not { } editor)
            {
                return;
            }

            editor.Focus(NavigationMethod.Tab);
            if (_pendingText is { } text)
            {
                _pendingText = null;
                editor.Text = text;
                editor.CaretIndex = text.Length;
            }
            else
            {
                editor.SelectAll();
            }
        },
        DispatcherPriority.Loaded);

    private void OnFocusGridRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () =>
        {
            if (_vm is { EditingRow: null } && GridHost.IsEffectivelyVisible && FocusedElement is not TextBox)
            {
                GridHost.Focus(NavigationMethod.Tab);
            }
        },
        DispatcherPriority.Loaded);

    private void OnScrollIntoViewRequested(object? sender, BudgetRowViewModel row)
    {
        if (BudgetRows.ContainerFromItem(row) is { } container)
        {
            container.BringIntoView();
        }
    }

    private void OnTargetFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () => InspectorHost.GetVisualDescendants().OfType<MoneyTextBox>().FirstOrDefault(b => b.Name == "TargetAmountBox")?.Focus(NavigationMethod.Tab),
        DispatcherPriority.Loaded);

    private MoneyTextBox? FindEditor(BudgetCategoryRowViewModel row) =>
        BudgetRows.ContainerFromItem(row)?.GetVisualDescendants().OfType<MoneyTextBox>().FirstOrDefault(b => b.Name == "AssignedEditor");

    // Leaving the editor with the mouse (or any other way) saves it.
    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is MoneyTextBox { Name: "AssignedEditor", DataContext: BudgetCategoryRowViewModel { IsEditing: true } row } editor)
        {
            editor.Commit();
            _vm?.CommitEdit(row, 0, keepEditing: false);
        }
    }

    // Clicks select a cell; a click on Assigned edits it; pressing an Available pill may start a drag.
    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<MoneyTextBox>(includeSelf: true) is not null)
        {
            return;     // clicks inside the editor belong to it
        }

        var cell = source.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Tag is string);
        if (cell?.DataContext is not BudgetRowViewModel row || !Enum.TryParse<BudgetColumn>((string)cell.Tag!, out var column))
        {
            return;
        }

        if (row is BudgetCategoryRowViewModel category && column == BudgetColumn.Assigned)
        {
            _vm.BeginEdit(category);
        }
        else
        {
            _vm.Select(row, column);
            GridHost.Focus(NavigationMethod.Pointer);
        }

        if (row is BudgetCategoryRowViewModel pill && column == BudgetColumn.Available)
        {
            _pressPoint = e.GetPosition(this);
            _pressSource = pill.Id;
            _pressFromReadyToAssign = false;
        }
    }

    private void OnPillPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressPoint = e.GetPosition(this);
            _pressSource = null;
            _pressFromReadyToAssign = true;
        }
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _pressPoint = null;
        if (_pressSource is null && !_pressFromReadyToAssign)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(CategoryFormat, _pressSource?.ToString("D") ?? string.Empty));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private static Guid? SourceOf(DragEventArgs e, out bool valid)
    {
        var text = e.DataTransfer.TryGetValue(CategoryFormat);
        valid = text is not null;
        return Guid.TryParse(text, out var id) ? id : null;
    }

    private static BudgetCategoryRowViewModel? RowAt(DragEventArgs e) =>
        (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("budgetRow"))?.DataContext as BudgetCategoryRowViewModel;

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        var source = SourceOf(e, out var valid);
        var row = RowAt(e);
        e.DragEffects = valid && row is not null && row.Id != source ? DragDropEffects.Move : DragDropEffects.None;
        foreach (var other in _vm?.Rows.OfType<BudgetCategoryRowViewModel>() ?? [])
        {
            other.IsDropTarget = ReferenceEquals(other, row) && e.DragEffects == DragDropEffects.Move;
        }
    }

    private void OnRowDragLeave(object? sender, DragEventArgs e)
    {
        foreach (var row in _vm?.Rows.OfType<BudgetCategoryRowViewModel>() ?? [])
        {
            row.IsDropTarget = false;
        }
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        OnRowDragLeave(sender, e);
        var source = SourceOf(e, out var valid);
        if (_vm is null || !valid || RowAt(e) is not { } target || target.Id == source)
        {
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        _ = _vm.DropAsync(source, target.Id);
    }

    private void OnReadyToAssignDragOver(object? sender, DragEventArgs e)
    {
        var source = SourceOf(e, out var valid);
        e.DragEffects = valid && source is not null ? DragDropEffects.Move : DragDropEffects.None;
        ReadyToAssignPill.Classes.Set("dropTarget", e.DragEffects == DragDropEffects.Move);
    }

    private void OnReadyToAssignDrop(object? sender, DragEventArgs e)
    {
        ReadyToAssignPill.Classes.Set("dropTarget", false);
        var source = SourceOf(e, out var valid);
        if (_vm is not null && valid && source is not null)
        {
            e.DragEffects = DragDropEffects.Move;
            _ = _vm.DropAsync(source, null);
        }
    }

    private void OnRowsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || e.Source is not Button button)
        {
            return;
        }

        if (button.Classes.Contains("expander") && button.DataContext is BudgetGroupRowViewModel group)
        {
            _vm.ToggleGroup(group);
            e.Handled = true;
        }
        else if (button.Classes.Contains("activityLink") && button.DataContext is BudgetCategoryRowViewModel row)
        {
            _vm.OpenActivity(row);
            e.Handled = true;
        }
    }
}
