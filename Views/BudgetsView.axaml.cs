using Avalonia.Controls;
using Avalonia.ReactiveUI;
using MyApp.ViewModels;
using ReactiveUI;

namespace MyApp.Views
{
    public partial class BudgetsView : ReactiveUserControl<BudgetsViewModel>
    {
        public BudgetsView()
        {
            InitializeComponent();
            // this.WhenActivated(disposables => { /* ... */ });
        }
    }
} 