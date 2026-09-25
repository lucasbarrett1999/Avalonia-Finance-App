using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="ViewModels.Dialogs.EncryptFileViewModel"/>; the passphrase box takes focus.</summary>
public partial class EncryptFileView : UserControl
{
    /// <summary>Creates the view.</summary>
    public EncryptFileView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PassphraseBox.Focus(NavigationMethod.Tab);
    }
}
