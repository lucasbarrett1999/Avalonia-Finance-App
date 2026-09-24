using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="ViewModels.Dialogs.PickerDialogViewModel"/>.</summary>
public partial class PickerDialogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PickerDialogView()
    {
        InitializeComponent();
        ItemList.DoubleTapped += (_, _) =>
        {
            if (DataContext is ViewModels.Dialogs.PickerDialogViewModel vm)
            {
                vm.ConfirmCommand.Execute(null);
            }
        };
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SearchBox.Focus(NavigationMethod.Tab);
    }
}
