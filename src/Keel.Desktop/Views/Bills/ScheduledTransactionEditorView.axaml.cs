using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Bills;

/// <summary>View for <see cref="ViewModels.Bills.ScheduledTransactionEditorViewModel"/> with the recurrence builder.</summary>
public partial class ScheduledTransactionEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ScheduledTransactionEditorView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SchedulePayeeBox.Focus(NavigationMethod.Tab);
    }
}
