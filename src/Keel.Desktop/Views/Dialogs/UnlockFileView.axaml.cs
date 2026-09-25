using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Dialogs;

/// <summary>View for <see cref="ViewModels.Dialogs.UnlockFileViewModel"/>; the passphrase box takes focus.</summary>
public partial class UnlockFileView : UserControl
{
    /// <summary>Creates the view.</summary>
    public UnlockFileView()
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
