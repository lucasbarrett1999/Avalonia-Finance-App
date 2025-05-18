using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using MyApp.Core.Entities;
using MyApp.Core.Interfaces;

namespace MyApp.ViewModels
{
    public class AccountsViewModel : ViewModelBase, IRoutableViewModel
    {
        // Nested ViewModel for Account items
        public class AccountViewModel : ViewModelBase
        {
            public int Id { get; }
            public string Name { get; }
            public string Type { get; }
            public decimal Balance { get; }
            public string? BankName { get; }
            public string? PlaidItemId { get; }
            public string BalanceFormatted => Balance.ToString("C");
            public string DisplayName => $"{Name} ({Type})";
            
            public AccountViewModel(Account account)
            {
                Id = account.Id;
                Name = account.Name;
                Type = account.Type;
                Balance = account.Balance;
                BankName = account.BankName ?? "Unknown Bank";
                PlaidItemId = account.PlaidItemId;
            }
        }
        public string UrlPathSegment => "accounts";
        public IScreen HostScreen { get; }

        // Services
        private readonly IPlaidService? _plaidService;
        private readonly IPlaidTokenManager? _tokenManager;
        private readonly IAccountSyncService? _accountSyncService;
        
        // Commands
        public ReactiveCommand<Unit, IRoutableViewModel> LinkAccountCommand { get; }
        public ReactiveCommand<string, IRoutableViewModel> UpdateAccountCommand { get; }
        public ReactiveCommand<string, IRoutableViewModel> RecoverAccountCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshAccountsCommand { get; }
        
        // Collections
        private ObservableCollection<AccountViewModel> _accounts = new();
        public ObservableCollection<AccountViewModel> Accounts
        {
            get => _accounts;
            set => this.RaiseAndSetIfChanged(ref _accounts, value);
        }
        
        // State
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }
        
        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }
        
        private bool _hasAccounts;
        public bool HasAccounts
        {
            get => _hasAccounts;
            set => this.RaiseAndSetIfChanged(ref _hasAccounts, value);
        }
        
        private bool _showEmptyState;
        public bool ShowEmptyState
        {
            get => _showEmptyState;
            set => this.RaiseAndSetIfChanged(ref _showEmptyState, value);
        }

        public AccountsViewModel(IScreen screen)
        {
            HostScreen = screen;
            
            // We don't have access to the PlaidService here through DI
            // This will be properly initialized in the factory
            _plaidService = null;
            
            // Initialize LinkAccountCommand (will be overridden in factory if DI is available)
            LinkAccountCommand = ReactiveCommand.CreateFromObservable(
                () => HostScreen.Router.Navigate.Execute(new PlaidLinkViewModel(HostScreen, null, null))
            );
            
            // These commands require actual implementations of IPlaidService and IPlaidTokenManager
            // So they're only initialized in the DI constructor
            UpdateAccountCommand = null;
            RecoverAccountCommand = null;
            RefreshAccountsCommand = ReactiveCommand.Create(() => { });
        }

        // This constructor will be used by the DI container
        public AccountsViewModel(
            IScreen screen, 
            IPlaidService plaidService,
            IPlaidTokenManager tokenManager,
            IAccountSyncService accountSyncService = null)
        {
            HostScreen = screen;
            _plaidService = plaidService;
            _tokenManager = tokenManager;
            _accountSyncService = accountSyncService;
            
            // Initialize LinkAccountCommand with proper dependencies
            LinkAccountCommand = ReactiveCommand.CreateFromObservable(
                () => HostScreen.Router.Navigate.Execute(
                    new PlaidLinkViewModel(HostScreen, plaidService, tokenManager, accountSyncService))
            );
            
            // Initialize update command - allows reconnecting a specific account
            UpdateAccountCommand = ReactiveCommand.CreateFromObservable<string, IRoutableViewModel>(
                plaidItemId => HostScreen.Router.Navigate.Execute(
                    new PlaidLinkViewModel(HostScreen, plaidService, tokenManager, plaidItemId, isRecoveryMode: false, accountSyncService))
            );
            
            // Initialize recover command - used when account is in an error state
            RecoverAccountCommand = ReactiveCommand.CreateFromObservable<string, IRoutableViewModel>(
                plaidItemId => HostScreen.Router.Navigate.Execute(
                    new PlaidLinkViewModel(HostScreen, plaidService, tokenManager, plaidItemId, isRecoveryMode: true, accountSyncService))
            );
            
            // Initialize refresh command
            RefreshAccountsCommand = ReactiveCommand.CreateFromTask(LoadAccountsAsync);
            
            // Load accounts when the view model is created
            LoadAccounts();
        }
        
        /// <summary>
        /// Refreshes the accounts data from the database
        /// </summary>
        public void LoadAccounts()
        {
            // For non-async methods
            _ = LoadAccountsAsync();
        }
        
        /// <summary>
        /// Loads accounts asynchronously
        /// </summary>
        public async Task LoadAccountsAsync()
        {
            IsLoading = true;
            StatusMessage = "Loading accounts...";
            
            try
            {
                if (_accountSyncService != null)
                {
                    // Get all accounts from the database
                    var accounts = await _accountSyncService.GetAllAccountsAsync();
                    
                    // Convert to view models
                    var accountViewModels = accounts
                        .OrderBy(a => a.BankName)
                        .ThenBy(a => a.Name)
                        .Select(a => new AccountViewModel(a))
                        .ToList();
                    
                    // Update the collection
                    Accounts.Clear();
                    foreach (var account in accountViewModels)
                    {
                        Accounts.Add(account);
                    }
                    
                    // Update state
                    HasAccounts = Accounts.Count > 0;
                    ShowEmptyState = !HasAccounts;
                    
                    StatusMessage = HasAccounts 
                        ? $"Loaded {Accounts.Count} accounts successfully." 
                        : "No accounts found. Link a bank to get started.";
                }
                else
                {
                    StatusMessage = "Account service not available.";
                    ShowEmptyState = true;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading accounts: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Error in LoadAccountsAsync: {ex}");
                ShowEmptyState = true;
            }
            finally
            {
                IsLoading = false;
            }
        }
        
        /// <summary>
        /// Syncs accounts for a specific Plaid item
        /// </summary>
        public async Task SyncAccountsForItemAsync(string accessToken, string plaidItemId)
        {
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(plaidItemId))
            {
                return;
            }
            
            try
            {
                if (_accountSyncService != null)
                {
                    // Synchronize accounts
                    await _accountSyncService.SyncAccountsAsync(accessToken, plaidItemId);
                    
                    // Refresh the accounts list
                    await LoadAccountsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error synchronizing accounts: {ex}");
                // Don't update UI - this is a background operation
            }
        }
    }
}