using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Bills;

/// <summary>View for <see cref="ViewModels.Bills.RecurringItemEditorViewModel"/>.</summary>
public partial class RecurringItemEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public RecurringItemEditorView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ItemPayeeBox.Focus(NavigationMethod.Tab);
    }
}
