using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Review;

namespace Keel.Desktop.Views;

/// <summary>
/// The review queue view. Code-behind wires the F-TXN-6 keyboard map (A, 1–9, C, S, T, R, D, J/K,
/// plus the arrow keys), keeps the queue list's selection on the focused transaction, and returns
/// keyboard focus to the queue after dialogs.
/// </summary>
public partial class ReviewView : UserControl
{
    private ReviewViewModel? _vm;
    private bool _syncingSelection;

    /// <summary>Creates the view.</summary>
    public ReviewView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        QueueList.SelectionChanged += OnQueueSelectionChanged;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.FocusRequested -= OnFocusRequested;
        }

        _vm = DataContext as ReviewViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.FocusRequested += OnFocusRequested;
            SyncSelection();
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Focus());

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReviewViewModel.Focused))
        {
            SyncSelection();
        }
    }

    private void SyncSelection()
    {
        if (_vm is null)
        {
            return;
        }

        _syncingSelection = true;
        QueueList.SelectedItem = _vm.Focused;
        _syncingSelection = false;
        if (_vm.Focused is { } focused)
        {
            QueueList.ScrollIntoView(focused);
        }
    }

    private void OnQueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm is null || QueueList.SelectedItem is not ReviewItemViewModel item || ReferenceEquals(item, _vm.Focused))
        {
            return;
        }

        _ = _vm.FocusIndexAsync(item.Index);
        Focus();
    }

    // F-TXN-6 keys. Text fields keep their keys; modified keys belong to the shell.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox
            || (e.Source as Visual)?.FindAncestorOfType<TextBox>() is not null || !_vm.ShowQueue)
        {
            return;
        }

        var number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
            _ => 0,
        };
        if (number > 0)
        {
            _ = _vm.PickSuggestionAsync(number);
            e.Handled = true;
            return;
        }

        Task? action = e.Key switch
        {
            Key.A => _vm.ApproveAsync(),
            Key.C => _vm.ChangeCategoryAsync(),
            Key.S => _vm.SplitAsync(),
            Key.T => _vm.MarkTransferAsync(),
            Key.R => _vm.CreateRuleAsync(),
            Key.D => _vm.DeleteAsync(),
            Key.J or Key.Down => _vm.NextAsync(),
            Key.K or Key.Up => _vm.PreviousAsync(),
            _ => null,
        };
        e.Handled = action is not null;
    }
}
