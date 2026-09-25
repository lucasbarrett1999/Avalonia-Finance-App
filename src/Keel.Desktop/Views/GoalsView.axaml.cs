using Avalonia.Controls;
using Avalonia.Input;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop.Views;

/// <summary>View for <see cref="ViewModels.GoalsViewModel"/>. N starts a new goal, 1 and 2 switch tabs (ShortcutRegistry).</summary>
public partial class GoalsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public GoalsView()
    {
        InitializeComponent();
        PageKeys.Attach(this, e =>
        {
            if (DataContext is not GoalsViewModel vm)
            {
                return false;
            }

            if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.None && vm.IsGoalsTab)
            {
                vm.NewGoalCommand.Execute(null);
                return true;
            }

            if (PageKeys.Digit(e) is { } digit and <= 2 && vm.DebtPayoff is not null)
            {
                vm.SelectedTab = digit == 1 ? GoalsTab.Goals : GoalsTab.DebtPayoff;
                return true;
            }

            return false;
        });
    }
}
