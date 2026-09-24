using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="ViewModels.Dialogs.RecordBalanceViewModel"/>.</summary>
public partial class RecordBalanceView : UserControl
{
    /// <summary>Creates the view.</summary>
    public RecordBalanceView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        BalanceBox.Focus(NavigationMethod.Tab);
    }
}
