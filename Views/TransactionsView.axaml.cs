using Avalonia.Controls;
using Avalonia.ReactiveUI;
using MyApp.ViewModels;
using ReactiveUI;

namespace MyApp.Views
{
    public partial class TransactionsView : ReactiveUserControl<TransactionsViewModel>
    {
        public TransactionsView()
        {
            InitializeComponent();
            // this.WhenActivated(disposables => { /* ... */ });
        }
    }
} 