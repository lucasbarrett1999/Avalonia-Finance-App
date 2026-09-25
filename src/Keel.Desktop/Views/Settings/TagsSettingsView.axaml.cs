using Avalonia;
using Avalonia.Controls;
using Keel.Desktop.ViewModels.Settings;

namespace Keel.Desktop.Views.Settings;

/// <summary>Settings → Tags; loads the list when it first appears.</summary>
public partial class TagsSettingsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TagsSettingsView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is TagsSettingsViewModel vm)
        {
            _ = vm.LoadAsync();
        }
    }
}
