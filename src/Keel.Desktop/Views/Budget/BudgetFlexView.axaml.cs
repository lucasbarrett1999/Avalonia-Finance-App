using Avalonia;
using Avalonia.Controls;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views.Budget;

/// <summary>The Flex view (F-BUD-6); code-behind places the "today" marker on the progress bar.</summary>
public partial class BudgetFlexView : UserControl
{
    private BudgetFlexViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public BudgetFlexView()
    {
        InitializeComponent();
        ProgressTrack.SizeChanged += (_, _) => PlaceMarker();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as BudgetFlexViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        PlaceMarker();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BudgetFlexViewModel.PaceMarker) or nameof(BudgetFlexViewModel.Summary))
        {
            PlaceMarker();
        }
    }

    // The marker shows how much of the month has passed: spending left of it is on pace.
    private void PlaceMarker()
    {
        var share = _vm?.PaceMarker ?? 0;
        PaceMarker.IsVisible = _vm is { HasSummary: true } && share > 0 && share < 1;
        PaceMarker.Margin = new Thickness(Math.Max(0, (ProgressTrack.Bounds.Width * share) - 1), 0, 0, 0);
    }
}
