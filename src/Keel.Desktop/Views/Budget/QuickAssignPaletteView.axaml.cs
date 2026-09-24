using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views.Budget;

/// <summary>View for <see cref="QuickAssignPaletteViewModel"/>: arrow keys choose, Enter applies, double-click applies.</summary>
public partial class QuickAssignPaletteView : UserControl
{
    /// <summary>Creates the view.</summary>
    public QuickAssignPaletteView()
    {
        InitializeComponent();
        OptionList.DoubleTapped += (_, _) => (DataContext as QuickAssignPaletteViewModel)?.ConfirmCommand.Execute(null);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Dispatcher.UIThread.Post(
            () => ((InputElement?)OptionList.ContainerFromIndex(Math.Max(0, OptionList.SelectedIndex)) ?? OptionList).Focus(NavigationMethod.Tab),
            DispatcherPriority.Loaded);
    }
}
