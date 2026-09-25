using Avalonia.Controls;
using Avalonia.Interactivity;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Bills;

namespace Keel.Desktop.Views;

/// <summary>
/// View for <see cref="BillsViewModel"/>: forwards list selection and the "review detected" link.
/// </summary>
public partial class BillsView : UserControl
{
    private BillsViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public BillsView()
    {
        InitializeComponent();
        Services.PageKeys.Attach(this, OnPageKey);
        BillsGrid.SelectionChanged += (_, _) =>
        {
            if (BillsGrid.SelectedItem is BillItemViewModel row && _vm is not null && _vm.Detail?.Item.Id != row.Id)
            {
                _vm.SelectItemCommand.Execute(row);
            }
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as BillsViewModel;
    }

    // Registered in ShortcutRegistry: 1/2/3 tabs, N new item, R run detection, Esc closes the detail panel.
    private bool OnPageKey(Avalonia.Input.KeyEventArgs e)
    {
        if (_vm is null)
        {
            return false;
        }

        if (Services.PageKeys.Digit(e) is { } digit and <= 3)
        {
            _vm.SelectedTab = (BillsTab)(digit - 1);
            return true;
        }

        if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None)
        {
            return false;
        }

        switch (e.Key)
        {
            case Avalonia.Input.Key.N:
                _vm.AddItemCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.R:
                _vm.RunDetectionCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.Escape when _vm.Detail is not null:
                _vm.CloseDetailCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnReviewDetected(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SelectedFilter = _vm.Filters.First(f => f.Value == BillsFilter.Detected);
            _vm.SelectedTab = BillsTab.List;
        }
    }
}
