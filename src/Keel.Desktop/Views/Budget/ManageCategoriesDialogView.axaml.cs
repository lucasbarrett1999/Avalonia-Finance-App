using Avalonia.Controls;
using Avalonia.Interactivity;
using Keel.Desktop.ViewModels.Budget;

namespace Keel.Desktop.Views.Budget;

/// <summary>View for <see cref="ManageCategoriesDialogViewModel"/>. A name saves when its field loses focus (or on Enter).</summary>
public partial class ManageCategoriesDialogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ManageCategoriesDialogView()
    {
        InitializeComponent();
        GroupList.AddHandler(LostFocusEvent, OnNameLostFocus);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: ManageItem item } box && box.Classes.Contains("inlineName") && item.IsEditable)
        {
            _ = item.RenameCommand.ExecuteAsync(null);
        }
    }
}
