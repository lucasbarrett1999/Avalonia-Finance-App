using Avalonia.Controls;
using Avalonia.ReactiveUI;
using MyApp.ViewModels;
using ReactiveUI;

namespace MyApp.Views
{
    public partial class GoalsView : ReactiveUserControl<GoalsViewModel>
    {
        public GoalsView()
        {
            InitializeComponent();
            // this.WhenActivated(disposables => { /* ... */ });
        }
    }
} 