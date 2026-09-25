using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Register;

namespace Keel.Desktop.Views;

/// <summary>
/// The register view. Code-behind only wires what XAML cannot: the F-ACC-2 keyboard map, grid
/// selection to the view model, split-row details, focus moves, and keeping the inline editor's
/// columns aligned with the grid's.
/// </summary>
public partial class AccountsView : UserControl
{
    private readonly Dictionary<DataGridRow, (RegisterRowViewModel Row, PropertyChangedEventHandler Handler)> _rowWatchers = [];
    private AccountsViewModel? _vm;
    private KeyModifiers _commandModifier = KeyModifiers.Control;
    private ScrollFrameMeter? _meter;

    /// <summary>Creates the view.</summary>
    public AccountsView()
    {
        InitializeComponent();
        RegisterGrid.SelectionChanged += (_, _) => _vm?.SetSelection(RegisterGrid.SelectedItems.OfType<RegisterRowViewModel>().ToList());
        RegisterGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        RegisterGrid.LoadingRow += OnLoadingRow;
        RegisterGrid.UnloadingRow += OnUnloadingRow;
        RegisterGrid.AddHandler(Button.ClickEvent, OnGridButtonClick);
        RegisterGrid.LayoutUpdated += (_, _) => SyncEditorColumns();
        EditorBar.LayoutUpdated += (_, _) => SyncEditorColumns();
        EditorBar.AddHandler(KeyDownEvent, OnEditorKeyDown);
        EditorBar.AddHandler(LostFocusEvent, OnEditorLostFocus);
        AddHandler(KeyDownEvent, OnViewKeyDown);
    }

    /// <summary>The register grid (tests drive it directly).</summary>
    public DataGrid Grid => RegisterGrid;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Same source as PlatformShortcuts: Cmd on macOS, Ctrl elsewhere.
        _commandModifier = PlatformShortcuts.FromCurrentPlatform().CommandModifiers;
        AttachMeter();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachMeter();
    }

    /// <summary>Frame timing for the Stats page while the register scrolls (PRD 4); tests read it.</summary>
    public ScrollFrameMeter? FrameMeter => _meter;

    // The register source reports each row the grid asks for; the meter times frames while that continues.
    private void AttachMeter()
    {
        DetachMeter();
        if (_vm?.Stats is not { } stats || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var meter = new ScrollFrameMeter(callback => top.RequestAnimationFrame(callback));
        var rows = _vm.Rows;
        meter.BurstEnded += (_, _) => stats.RecordScroll(meter, rows.Count);
        rows.RowRequested = meter.Activity;
        _meter = meter;
    }

    private void DetachMeter()
    {
        if (_meter is not null && _vm is not null)
        {
            _vm.Rows.RowRequested = null;
        }

        _meter = null;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.SelectIndexRequested -= OnSelectIndexRequested;
            _vm.EditorOpened -= OnEditorOpened;
            _vm.EditorClosed -= OnEditorClosed;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        DetachMeter();
        _vm = DataContext as AccountsViewModel;
        AttachMeter();

        if (_vm is not null)
        {
            _vm.SelectIndexRequested += OnSelectIndexRequested;
            _vm.EditorOpened += OnEditorOpened;
            _vm.EditorClosed += OnEditorClosed;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateAccountColumn();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountsViewModel.IsAllAccounts) or nameof(AccountsViewModel.Account))
        {
            UpdateAccountColumn();
            _meter?.Reset(); // another register: frame times start over
        }
    }

    private void UpdateAccountColumn()
    {
        // Column 1 is the Account column; only the All Accounts register shows it.
        if (RegisterGrid.Columns.Count > 1)
        {
            RegisterGrid.Columns[1].IsVisible = _vm?.IsAllAccounts ?? true;
        }
    }

    // Grid keys (F-ACC-2): N new, Enter edit, C cleared, A approve, Delete delete, Esc clear selection.
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                _ = _vm.NewTransactionCommand.ExecuteAsync(null);
                break;
            case Key.Enter:
                _ = _vm.EditSelectedCommand.ExecuteAsync(null);
                break;
            case Key.C:
                _ = _vm.ToggleClearedCommand.ExecuteAsync(null);
                break;
            case Key.A:
                _ = _vm.ApproveCommand.ExecuteAsync(null);
                break;
            case Key.Delete:
                if (_vm.DeleteSelectedCommand.CanExecute(null))
                {
                    _ = _vm.DeleteSelectedCommand.ExecuteAsync(null);
                }

                break;
            case Key.Escape:
                RegisterGrid.SelectedItems.Clear();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // N anywhere in the register outside a text field starts a new transaction.
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key != Key.N
            || e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<TextBox>() is not null || _vm.IsEditing)
        {
            return;
        }

        _ = _vm.NewTransactionCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    // Editor keys: Enter saves, Ctrl/Cmd+Enter saves and starts another, Esc cancels. Runs after the
    // focused field (a MoneyTextBox commits its math first; an open autocomplete list takes Enter).
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Return)
        {
            if (e.KeyModifiers == _commandModifier)
            {
                CommitFocusedMoney();
                _ = _vm.SaveAndNewCommand.ExecuteAsync(null);
                e.Handled = true;
            }
            else if (e.KeyModifiers == KeyModifiers.None && e.Source is not Button)
            {
                _ = _vm.SaveCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            _vm.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CommitFocusedMoney()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is MoneyTextBox money)
        {
            money.Commit();
        }
    }

    // Leaving the payee box on a new row pre-fills the payee's last category and memo.
    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Editor is { } editor && e.Source is Visual source
            && (source as AutoCompleteBox ?? source.FindAncestorOfType<AutoCompleteBox>()) is { Name: "EditorPayee" })
        {
            _ = editor.ApplyPayeeSuggestionAsync();
        }
    }

    private void OnGridButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ToggleButton toggle && toggle.Classes.Contains("expander") && toggle.DataContext is RegisterRowViewModel row)
        {
            row.IsExpanded = !row.IsExpanded;
            e.Handled = true;
        }
    }

    // Split details follow the row's IsExpanded while a grid row shows it (rows are recycled).
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        OnUnloadingRow(sender, e);
        if (e.Row.DataContext is not RegisterRowViewModel row)
        {
            return;
        }

        var gridRow = e.Row;
        void Handler(object? s, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(RegisterRowViewModel.IsExpanded) or nameof(RegisterRowViewModel.Row))
            {
                gridRow.AreDetailsVisible = row.IsExpanded && row.IsSplit;
            }
        }

        row.PropertyChanged += Handler;
        _rowWatchers[gridRow] = (row, Handler);
        gridRow.AreDetailsVisible = row.IsExpanded && row.IsSplit;
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (_rowWatchers.Remove(e.Row, out var watcher))
        {
            watcher.Row.PropertyChanged -= watcher.Handler;
        }
    }

    private void OnSelectIndexRequested(object? sender, int index)
    {
        if (_vm is null || index < 0 || index >= _vm.Rows.Count)
        {
            return;
        }

        RegisterGrid.SelectedIndex = index;
        RegisterGrid.ScrollIntoView(_vm.Rows[index], null);
        if (!_vm.IsEditing)
        {
            RegisterGrid.Focus(NavigationMethod.Tab);
        }
    }

    private void OnEditorOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () =>
        {
            var payee = EditorHost.GetVisualDescendants().OfType<AutoCompleteBox>().FirstOrDefault(b => b.Name == "EditorPayee");
            var box = payee?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            (box ?? (InputElement?)payee)?.Focus(NavigationMethod.Tab);
            box?.SelectAll();
        },
        DispatcherPriority.Loaded);

    private void OnEditorClosed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () => RegisterGrid.Focus(NavigationMethod.Tab),
        DispatcherPriority.Loaded);

    // Aligns the inline editor (and split lines) with the grid's actual column widths.
    private void SyncEditorColumns()
    {
        if (RegisterGrid.Columns.Count == 0)
        {
            return;
        }

        var rows = Enumerable.Empty<Grid>();
        if (EditorBar.IsVisible)
        {
            rows = EditorBar.GetVisualDescendants().OfType<Grid>();
        }

        if (_rowWatchers.Values.Any(w => w.Row.IsExpanded))
        {
            rows = rows.Concat(RegisterGrid.GetVisualDescendants().OfType<Grid>());
        }

        foreach (var row in rows.Where(g => g.Classes.Contains("editorRow")))
        {
            var count = Math.Min(row.ColumnDefinitions.Count, RegisterGrid.Columns.Count);
            for (var i = 0; i < count; i++)
            {
                var column = RegisterGrid.Columns[i];
                var width = column.IsVisible ? column.ActualWidth : 0;
                if (i == count - 1 || double.IsNaN(width) || width <= 0 && column.IsVisible)
                {
                    continue;
                }

                var definition = row.ColumnDefinitions[i];
                if (!definition.Width.IsAbsolute || Math.Abs(definition.Width.Value - width) > 0.5)
                {
                    definition.Width = new GridLength(width);
                }
            }
        }
    }
}
