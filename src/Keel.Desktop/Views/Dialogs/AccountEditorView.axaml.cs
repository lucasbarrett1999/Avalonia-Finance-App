using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="ViewModels.Dialogs.AccountEditorViewModel"/>.</summary>
public partial class AccountEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public AccountEditorView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus(NavigationMethod.Tab);
    }
}
