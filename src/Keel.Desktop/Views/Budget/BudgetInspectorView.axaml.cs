using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views.Budget;

/// <summary>View for <see cref="BudgetInspectorViewModel"/>.</summary>
public partial class BudgetInspectorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public BudgetInspectorView()
    {
        InitializeComponent();
        NoteBox.LostFocus += OnNoteLostFocus;
        TargetAmountBox.AddHandler(KeyDownEvent, OnTargetAmountKeyDown);
    }

    // Enter saves the target after the amount box has evaluated its text.
    private void OnTargetAmountKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None && DataContext is BudgetInspectorViewModel vm)
        {
            _ = vm.SaveTargetCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    // The note saves when the field loses focus (F-BUD-7).
    private void OnNoteLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BudgetInspectorViewModel vm)
        {
            _ = vm.SaveNoteCommand.ExecuteAsync(null);
        }
    }
}
