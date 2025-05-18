using Avalonia.Controls;
using Avalonia.ReactiveUI;
using MyApp.ViewModels;
using ReactiveUI;

namespace MyApp.Views
{
    public partial class AccountsView : ReactiveUserControl<AccountsViewModel>
    {
        public AccountsView()
        {
            InitializeComponent();
            // this.WhenActivated(disposables => { /* ... */ });
        }
    }
} 