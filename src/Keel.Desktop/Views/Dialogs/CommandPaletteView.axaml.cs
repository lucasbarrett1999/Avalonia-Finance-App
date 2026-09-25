using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="CommandPaletteViewModel"/>: typing filters, ↑/↓ move, Enter runs, Esc closes.</summary>
public partial class CommandPaletteView : UserControl
{
    /// <summary>Creates the view.</summary>
    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ResultList.DoubleTapped += (_, _) => (DataContext as CommandPaletteViewModel)?.ConfirmCommand.Execute(null);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QueryBox.Focus(NavigationMethod.Tab);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter or Key.Return:
                vm.ConfirmCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CancelCommand.Execute(null);
                e.Handled = true;
                break;
        }

        if (e.Handled && vm.Selected is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }
    }
}
