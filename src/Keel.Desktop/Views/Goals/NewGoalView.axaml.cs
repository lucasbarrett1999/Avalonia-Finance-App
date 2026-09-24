using Avalonia.Controls;

namespace Keel.Desktop.Views.Goals;

/// <summary>View for <see cref="ViewModels.Goals.NewGoalViewModel"/>.</summary>
public partial class NewGoalView : UserControl
{
    /// <summary>Creates the view and focuses the name box.</summary>
    public NewGoalView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => GoalNameBox.Focus();
    }
}
