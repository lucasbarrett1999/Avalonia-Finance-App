using Avalonia;
using Avalonia.Controls;
using Keel.Desktop.ViewModels.Rules;

namespace Keel.Desktop.Views.Rules;

/// <summary>Settings → Payees; loads the list when it first appears.</summary>
public partial class PayeesPanel : UserControl
{
    /// <summary>Creates the view.</summary>
    public PayeesPanel()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is PayeesViewModel vm)
        {
            _ = vm.EnsureLoadedAsync();
        }
    }
}
