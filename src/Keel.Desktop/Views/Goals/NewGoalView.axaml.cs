using Avalonia.Controls;
using Avalonia.Input;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.Views.Goals;

/// <summary>View for <see cref="ViewModels.Goals.NewGoalViewModel"/>.</summary>
public partial class NewGoalView : UserControl
{
    /// <summary>Creates the view and focuses the name box.</summary>
    public NewGoalView()
    {
        InitializeComponent();
        GoalAmountBox.AddHandler(KeyDownEvent, OnMoneyKeyDown);
        AttachedToVisualTree += (_, _) => GoalNameBox.Focus();
    }

    // Enter confirms after the amount box has evaluated its text: a KeyBinding would run before the
    // box commits and save the previous value (see CLAUDE.md, MoneyTextBox forms).
    private void OnMoneyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None && DataContext is DialogViewModel vm)
        {
            _ = vm.ConfirmCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
