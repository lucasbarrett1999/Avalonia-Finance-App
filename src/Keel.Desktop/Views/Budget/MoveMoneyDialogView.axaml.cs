using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views.Budget;

/// <summary>View for <see cref="MoveMoneyDialogViewModel"/>: focus starts on the first empty side, else on the amount.</summary>
public partial class MoveMoneyDialogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public MoveMoneyDialogView()
    {
        InitializeComponent();
        AmountBox.AddHandler(KeyDownEvent, OnAmountKeyDown);
    }

    // Enter confirms after the amount box has evaluated its text (its own key handling runs first).
    private void OnAmountKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None && DataContext is MoveMoneyDialogViewModel vm)
        {
            _ = vm.ConfirmCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Control target = DataContext switch
        {
            MoveMoneyDialogViewModel { From: null } => FromBox,
            MoveMoneyDialogViewModel { To: null } => ToBox,
            _ => AmountBox,
        };
        target.Focus(NavigationMethod.Tab);
    }
}
