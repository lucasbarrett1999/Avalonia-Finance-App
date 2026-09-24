using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Keel.Desktop.Views.Import;

/// <summary>View for <see cref="ViewModels.Import.CsvMappingViewModel"/>.</summary>
public partial class CsvMappingView : UserControl
{
    /// <summary>Creates the view.</summary>
    public CsvMappingView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        DateColumnBox.Focus(NavigationMethod.Tab);
    }
}
