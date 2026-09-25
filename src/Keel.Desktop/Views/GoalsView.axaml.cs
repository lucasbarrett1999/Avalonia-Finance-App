using Avalonia.Controls;
using Avalonia.Input;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop.Views;

/// <summary>View for <see cref="ViewModels.GoalsViewModel"/>. N starts a new goal (ShortcutRegistry).</summary>
public partial class GoalsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public GoalsView()
    {
        InitializeComponent();
        PageKeys.Attach(this, e =>
        {
            if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.None && DataContext is GoalsViewModel vm)
            {
                vm.NewGoalCommand.Execute(null);
                return true;
            }

            return false;
        });
    }
}
