using System;
// using Avalonia.Controls; // Not strictly needed here if IViewFor is from ReactiveUI
using MyApp.ViewModels;
using MyApp.Views;
using ReactiveUI;

namespace MyApp
{
    public class AppViewLocator : ReactiveUI.IViewLocator
    {
        // Implicit implementation matching the interface definition:
        // - T is unconstrained here (matches interface)
        // - viewModel is T?
        // - contract is string?
        // - return type is IViewFor?
        public IViewFor? ResolveView<T>(T? viewModel, string? contract = null)
        {
            // Handle the case where viewModel could be null, especially if T is a reference type.
            // Or if T is a nullable value type (e.g., int?), it could also be null.
            if (viewModel == null)
            {
                return null;
            }

            // The switch statement will then operate on a non-null viewModel.
            // Our specific view models (DashboardViewModel, etc.) are classes.
            return viewModel switch
            {
                DashboardViewModel dashboardViewModel => new DashboardView { DataContext = dashboardViewModel },
                AccountsViewModel accountsViewModel => new AccountsView { DataContext = accountsViewModel },
                BudgetsViewModel budgetsViewModel => new BudgetsView { DataContext = budgetsViewModel },
                TransactionsViewModel transactionsViewModel => new TransactionsView { DataContext = transactionsViewModel },
                GoalsViewModel goalsViewModel => new GoalsView { DataContext = goalsViewModel },
                PlaidLinkViewModel plaidLinkViewModel => new PlaidLinkView { DataContext = plaidLinkViewModel },
                // Use typeof(T) is more robust here as viewModel is non-null.
                _ => throw new ArgumentOutOfRangeException(nameof(viewModel), $"No view found for ViewModel type: {typeof(T).FullName}.")
            };
        }
    }
} 