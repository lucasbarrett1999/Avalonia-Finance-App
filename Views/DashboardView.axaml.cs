using Avalonia.Controls;
using Avalonia.ReactiveUI; // For ReactiveUserControl
using MyApp.ViewModels; // Assuming your ViewModels are in MyApp.ViewModels
using ReactiveUI; // For WhenActivated

namespace MyApp.Views;

public partial class DashboardView : ReactiveUserControl<DashboardViewModel>
{
    public DashboardView()
    {
        InitializeComponent();
        // Optional: ViewModel activation logic
        // this.WhenActivated(disposables => { /* ... */ });
    }
} 