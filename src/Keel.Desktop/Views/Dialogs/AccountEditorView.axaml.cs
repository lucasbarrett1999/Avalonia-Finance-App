using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="ViewModels.Dialogs.AccountEditorViewModel"/>.</summary>
public partial class AccountEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public AccountEditorView()
    {
        InitializeComponent();
        BalanceBox.AddHandler(KeyDownEvent, OnMoneyKeyDown);
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

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus(NavigationMethod.Tab);
    }
}
