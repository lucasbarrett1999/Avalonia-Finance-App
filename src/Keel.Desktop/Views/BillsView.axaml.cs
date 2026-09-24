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

    private void OnReviewDetected(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SelectedFilter = _vm.Filters.First(f => f.Value == BillsFilter.Detected);
            _vm.SelectedTab = BillsTab.List;
        }
    }
}
