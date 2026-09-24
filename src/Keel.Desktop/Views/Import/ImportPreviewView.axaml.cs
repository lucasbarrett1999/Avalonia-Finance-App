using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Import;

/// <summary>View for <see cref="ViewModels.Import.ImportPreviewViewModel"/>.</summary>
public partial class ImportPreviewView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ImportPreviewView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ImportButton.Focus(NavigationMethod.Tab);
    }
}
