using System;
using System.Reactive;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using MyApp.Core.Interfaces;
using ReactiveUI;
using Going.Plaid;
using Going.Plaid.Entity;
using Microsoft.Extensions.Configuration;

namespace MyApp.ViewModels
{
    public class PlaidLinkViewModel : ViewModelBase, IRoutableViewModel
    {
        // Required by IRoutableViewModel
        public string UrlPathSegment => "plaid-link";
        public IScreen HostScreen { get; }

        // Services
        private readonly IPlaidService _plaidService;
        private readonly IPlaidTokenManager _tokenManager;
        private readonly IAccountSyncService _accountSyncService;

        // State properties
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }
        
        private bool _isWebViewVisible;
        public bool IsWebViewVisible
        {
            get => _isWebViewVisible;
            set => this.RaiseAndSetIfChanged(ref _isWebViewVisible, value);
        }
        
        private string _linkUrl = string.Empty;
        public string LinkUrl
        {
            get => _linkUrl;
            set => this.RaiseAndSetIfChanged(ref _linkUrl, value);
        }
        
        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }
        
        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }
        
        private bool _isSuccess;
        public bool IsSuccess
        {
            get => _isSuccess;
            set => this.RaiseAndSetIfChanged(ref _isSuccess, value);
        }
        
        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set => this.RaiseAndSetIfChanged(ref _isError, value);
        }

        // Commands
        public IAsyncRelayCommand InitiateLinkCommand { get; }
        public IAsyncRelayCommand CancelCommand { get; }
        public IAsyncRelayCommand RetryCommand { get; }
        public ReactiveCommand<Unit, IRoutableViewModel> GoBackCommand { get; }

        /// <summary>
        /// Constructor for creating a new bank connection
        /// </summary>
        public PlaidLinkViewModel(
            IScreen screen,
            IPlaidService plaidService,
            IPlaidTokenManager tokenManager,
            IAccountSyncService accountSyncService = null)
        {
            HostScreen = screen ?? throw new ArgumentNullException(nameof(screen));
            _plaidService = plaidService ?? throw new ArgumentNullException(nameof(plaidService));
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _accountSyncService = accountSyncService; // Optional, can be null

            // Initialize properties
            IsLoading = false;
            IsWebViewVisible = false;
            IsSuccess = false;
            IsError = false;
            FlowType = LinkFlowType.NewConnection;

            // Initialize commands
            InitiateLinkCommand = new AsyncRelayCommand(() => InitiatePlaidLinkAsync(forceRefresh: false));
            CancelCommand = new AsyncRelayCommand(CancelLinkingAsync);
            RetryCommand = new AsyncRelayCommand(() => InitiatePlaidLinkAsync(forceRefresh: true));
            GoBackCommand = HostScreen.Router.NavigateBack;

            // Auto-start the linking process when the view model is created
            InitiateLinkCommand.ExecuteAsync(null);
        }
        
        /// <summary>
        /// Constructor for updating or reconnecting an existing bank connection
        /// </summary>
        public PlaidLinkViewModel(
            IScreen screen,
            IPlaidService plaidService,
            IPlaidTokenManager tokenManager,
            string existingPlaidItemId,
            bool isRecoveryMode = false,
            IAccountSyncService accountSyncService = null)
            : this(screen, plaidService, tokenManager, accountSyncService)
        {
            _existingItemId = existingPlaidItemId;
            FlowType = isRecoveryMode ? LinkFlowType.RecoverMode : LinkFlowType.UpdateMode;
            
            // Override the status message for update/recovery modes
            StatusMessage = isRecoveryMode 
                ? "Attempting to recover your bank connection..." 
                : "Preparing to update your bank connection...";
        }

        // Adding new enums for LinkFlow mode and state
        public enum LinkFlowType
        {
            NewConnection,
            UpdateMode,
            RecoverMode
        }
        
        private LinkFlowType _flowType = LinkFlowType.NewConnection;
        public LinkFlowType FlowType
        {
            get => _flowType;
            set => this.RaiseAndSetIfChanged(ref _flowType, value);
        }
        
        private string? _existingItemId;
        
        /// <summary>
        
        /// Initiates the Plaid Link flow by requesting a link token
        
        /// and preparing the WebView
        /// </summary>
        /// <param name="forceRefresh">Whether to force a new token even if one exists in cache</param>
        private async Task InitiatePlaidLinkAsync(bool forceRefresh = false)
        
        {
            
            try
            {
                // Reset state
                IsWebViewVisible = false;
                IsSuccess = false;
                IsError = false;
                ErrorMessage = string.Empty;
                StatusMessage = "Preparing to link your accounts...";
                IsLoading = true;
                
                // Add detailed logging
                System.Diagnostics.Debug.WriteLine($"Starting Plaid Link flow of type: {FlowType}");
                
                // Test the API keys specifically with Plaid
                StatusMessage = "Validating Plaid API credentials...";
                System.Diagnostics.Debug.WriteLine("Validating Plaid configuration...");
                
                // Run a direct test of the Plaid credentials
                var testResult = await TestPlaidCredentials();
                if (!testResult)
                {
                    IsError = true;
                    ErrorMessage = "Plaid credentials test failed. Check the console logs for detailed error information.";
                    return;
                }
                
                // Add diagnostic logging
                await DiagnosePlaidConfiguration();
                
                // Try to directly create a Plaid client as a test
                try 
                {
                    System.Diagnostics.Debug.WriteLine("Testing direct Plaid SDK usage...");
                    var testEnv = Going.Plaid.Environment.Sandbox;
                    var testClient = new Going.Plaid.PlaidClient(testEnv, "test_id", "test_secret");
                    System.Diagnostics.Debug.WriteLine("Successfully created test PlaidClient instance");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error creating test PlaidClient: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                
                // First check if the service is initialized - capture any errors
                string? errorDetails = null;
                bool isConfigured = false;
                
                try 
                {
                    isConfigured = await _plaidService.IsConfiguredCorrectlyAsync();
                    System.Diagnostics.Debug.WriteLine($"Plaid configured correctly: {isConfigured}");
                }
                catch (Exception ex)
                {
                    errorDetails = $"Exception during Plaid validation: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine(errorDetails);
                    System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                
                if (!isConfigured)
                {
                    IsError = true;
                    ErrorMessage = $"Plaid integration validation failed. {errorDetails ?? "The API keys may be incorrect or the service may be unavailable."}";
                    System.Diagnostics.Debug.WriteLine("Error: Plaid API validation failed");
                    
                    // Check if Going.Plaid assembly is actually available
                    try
                    {
                        var assembly = typeof(Going.Plaid.PlaidClient).Assembly;
                        System.Diagnostics.Debug.WriteLine($"Going.Plaid assembly is loaded: {assembly.FullName}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error accessing Going.Plaid assembly: {ex.Message}");
                        ErrorMessage = "The Plaid SDK (Going.Plaid) cannot be found or is not properly referenced. This is likely a configuration issue.";
                    }
                    
                    return;
                }
                
                System.Diagnostics.Debug.WriteLine("Plaid configuration validated successfully");
                
                // Show quick feedback to user
                StatusMessage = "Plaid API credentials validated successfully...";
                
                // Get the environment for user feedback
                var plaidEnv = await _plaidService.GetPlaidEnvironmentAsync();
                System.Diagnostics.Debug.WriteLine($"Using Plaid environment: {plaidEnv ?? "unknown"}");
                
                // Different token generation based on flow type
                string? linkToken = null;
                
                // Get the current user ID (for now, use a placeholder)
                
                string userId = "user_" + DateTime.Now.Ticks.ToString();
                
                switch (FlowType)
                {
                    case LinkFlowType.NewConnection:
                        // Standard link token for new connections
                        StatusMessage = "Creating secure connection to your bank...";
                        System.Diagnostics.Debug.WriteLine($"Requesting link token for user ID: {userId}");
                
                        linkToken = await _plaidService.CreateLinkTokenAsync(userId);
                        System.Diagnostics.Debug.WriteLine($"Link token received: {(string.IsNullOrEmpty(linkToken) ? "NULL" : "Valid token")}");
                        break;
                        
                    case LinkFlowType.UpdateMode:
                        if (!string.IsNullOrEmpty(_existingItemId))
                        {
                            // In a real implementation, we would get the access token and
                            // create an update mode link token with the existing item ID
                            StatusMessage = "Preparing to update your bank connection...";

                            // This would call a different method, like:
                            // linkToken = await _plaidService.CreateUpdateLinkTokenAsync(_existingItemId);
                            
                            // For now, fall back to standard flow
                            linkToken = await _plaidService.CreateLinkTokenAsync(userId);
                        }
                        else
                        {
                            // Fall back to standard flow if no item ID provided
                            StatusMessage = "Creating secure connection to your bank...";

                            linkToken = await _plaidService.CreateLinkTokenAsync(userId);
                        }
                        break;
                        
                    case LinkFlowType.RecoverMode:
                        // In a real implementation, we would handle special recovery flow
                        StatusMessage = "Attempting to recover your bank connection...";

                        // This would call a different method, like:
                        // linkToken = await _plaidService.CreateRecoveryLinkTokenAsync(userId);
                        
                        // For now, fall back to standard flow
                        linkToken = await _plaidService.CreateLinkTokenAsync(userId);
                        break;
                }
                
                // Check if token was created successfully
                if (string.IsNullOrEmpty(linkToken))
                {
                    IsError = true;
                    ErrorMessage = "Unable to create a secure connection to your bank. The Plaid Link token could not be created. This could be due to invalid credentials or a network issue.";
                    System.Diagnostics.Debug.WriteLine("Error: Failed to create Plaid link token");
                    
                    // In sandbox/development mode, provide more detailed debugging info
                    if (plaidEnv?.ToLowerInvariant() == "sandbox" || plaidEnv?.ToLowerInvariant() == "development")
                    {
                        ErrorMessage += "\n\nDebugging Info (Dev/Sandbox only):" +
                            "\n- Verify Client ID and Secret in appsettings.Local.json" +
                            "\n- Confirm Environment is set to 'sandbox'" +
                            "\n- Check ClientName is properly set" +
                            "\n- Ensure network connectivity to api.sandbox.plaid.com";
                    }
                    
                    return;
                }
                
                System.Diagnostics.Debug.WriteLine("Successfully created Plaid link token");
                
                // Log the token creation success (partial token for security)
                var tokenPrefix = linkToken.Length > 10 ? linkToken.Substring(0, 5) + "..." : "[invalid token]";
                System.Diagnostics.Debug.WriteLine($"Link token created successfully: {tokenPrefix}");
                
                // Configure the WebView with Plaid Link URL and token
                
                // The Link URL format is specific to how Plaid's web-based Link framework is hosted
                
                LinkUrl = $"https://cdn.plaid.com/link/v2/stable/link.html?token={linkToken}&isWebview=true&isElementsBrowser=true";
                
                
                // Adding isElementsBrowser=true helps with WebView compatibility
                
                StatusMessage = "Please select your bank and follow the instructions to connect your accounts.";
                
                
                // Show the WebView
                
                IsWebViewVisible = true;
            
            }
            
            catch (Exception ex)
            {
                IsError = true;
                ErrorMessage = $"Error initializing account linking: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Exception in InitiatePlaidLinkAsync: {ex}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    System.Diagnostics.Debug.WriteLine($"Inner stack trace: {ex.InnerException.StackTrace}");
                }
            }
            
            finally
            
            {
                
                IsLoading = false;
            
            }
        
        }

        /// <summary>
        /// Handles the successful completion of the Plaid Link flow
        /// </summary>
        public async Task HandlePlaidSuccessAsync(string publicToken, string institutionName)
        {
            if (string.IsNullOrEmpty(publicToken))
            {
                IsError = true;
                ErrorMessage = "Invalid response from bank connection process.";
                System.Diagnostics.Debug.WriteLine("HandlePlaidSuccessAsync received empty public token");
                return;
            }

            try
            {
                IsLoading = true;
                StatusMessage = "Finalizing connection...";
                IsWebViewVisible = false;
                
                // Log the success event
                System.Diagnostics.Debug.WriteLine($"Plaid Link Success: Institution={institutionName}, Token={publicToken.Substring(0, 5)}...");
                
                // For update mode, handle differently
                if (FlowType == LinkFlowType.UpdateMode && !string.IsNullOrEmpty(_existingItemId))
                {
                    try 
                    {
                        // Get the existing PlaidItem
                        var existingItem = await _tokenManager.GetPlaidItemAsync(_existingItemId);
                        
                        if (existingItem != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Update mode: Updating item {_existingItemId} with new token");
                            
                            // Exchange the public token (this will create a new access token)
                            var (newAccessToken, newItemId) = await _plaidService.ExchangePublicTokenAsync(
                                publicToken,
                                institutionName);
                                
                            if (!string.IsNullOrEmpty(newAccessToken))
                            {
                                // Update the existing item with the new access token
                                await _tokenManager.RotateAccessTokenAsync(existingItem.Id, newAccessToken);
                                
                                // Success - return with a specific message
                                IsSuccess = true;
                                StatusMessage = $"Successfully updated connection to {institutionName}.";
                                System.Diagnostics.Debug.WriteLine($"Successfully updated Plaid Item: {_existingItemId}");
                                
                                // Sync updated accounts
                                try
                                {
                                    if (!string.IsNullOrEmpty(newAccessToken) && _accountSyncService != null)
                                    {
                                        // Synchronize accounts to the local database
                                        System.Diagnostics.Debug.WriteLine($"Synchronizing accounts for updated item {_existingItemId}");
                                        var syncedAccounts = await _accountSyncService.SyncAccountsAsync(newAccessToken, _existingItemId);
                                        int accountCount = syncedAccounts.Count();
                                        System.Diagnostics.Debug.WriteLine($"Synchronized {accountCount} accounts after update from {institutionName}");
                                    }
                                    else if (!string.IsNullOrEmpty(newAccessToken))
                                    {
                                        // If the sync service is not available, at least fetch and log
                                        System.Diagnostics.Debug.WriteLine($"Fetching accounts for updated item {_existingItemId} (sync service not available)");
                                        var accounts = await _plaidService.GetAccountsAsync(newAccessToken);
                                        int accountCount = 0;
                                        if (accounts != null) accountCount = accounts.ToList().Count;
                                        System.Diagnostics.Debug.WriteLine($"Retrieved {accountCount} accounts after update from {institutionName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // Log but don't show error to user - this is just a background refresh
                                    System.Diagnostics.Debug.WriteLine($"Error syncing accounts after update: {ex.Message}");
                                }
                                
                                await Task.Delay(1500);
                                HostScreen.Router.NavigateBack.Execute();
                                return;
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Update mode: Existing item {_existingItemId} not found");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error during item update: {ex.Message}");
                    }
                    
                    // If the update logic fails, proceed with standard flow as fallback
                    System.Diagnostics.Debug.WriteLine($"Update mode: Falling back to standard flow");
                }
                
                // For recovery mode, similar to update but clears error state
                if (FlowType == LinkFlowType.RecoverMode && !string.IsNullOrEmpty(_existingItemId))
                {
                    try 
                    {
                        // Get the existing PlaidItem
                        var existingItem = await _tokenManager.GetPlaidItemAsync(_existingItemId);
                        
                        if (existingItem != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Recovery mode: Recovering item {_existingItemId} with new token");
                            
                            // Exchange the public token (this will create a new access token)
                            var (newAccessToken, newItemId) = await _plaidService.ExchangePublicTokenAsync(
                                publicToken,
                                institutionName);
                                
                            if (!string.IsNullOrEmpty(newAccessToken))
                            {
                                // Update the existing item with the new access token
                                await _tokenManager.RotateAccessTokenAsync(existingItem.Id, newAccessToken);
                                
                                // Clear any error state
                                await _tokenManager.ClearItemErrorAsync(existingItem.Id);
                                
                                // Success - return with a specific message
                                IsSuccess = true;
                                StatusMessage = $"Successfully recovered connection to {institutionName}.";
                                System.Diagnostics.Debug.WriteLine($"Successfully recovered Plaid Item: {_existingItemId}");
                                
                                // Sync recovered accounts
                                try
                                {
                                    if (!string.IsNullOrEmpty(newAccessToken) && _accountSyncService != null)
                                    {
                                        // Synchronize accounts to the local database
                                        System.Diagnostics.Debug.WriteLine($"Synchronizing accounts for recovered item {_existingItemId}");
                                        var syncedAccounts = await _accountSyncService.SyncAccountsAsync(newAccessToken, _existingItemId);
                                        int accountCount = syncedAccounts.Count();
                                        System.Diagnostics.Debug.WriteLine($"Synchronized {accountCount} accounts after recovery from {institutionName}");
                                    }
                                    else if (!string.IsNullOrEmpty(newAccessToken))
                                    {
                                        // If the sync service is not available, at least fetch and log
                                        System.Diagnostics.Debug.WriteLine($"Fetching accounts for recovered item {_existingItemId} (sync service not available)");
                                        var accounts = await _plaidService.GetAccountsAsync(newAccessToken);
                                        int accountCount = 0;
                                        if (accounts != null) accountCount = accounts.ToList().Count;
                                        System.Diagnostics.Debug.WriteLine($"Retrieved {accountCount} accounts after recovery from {institutionName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // Log but don't show error to user - this is just a background refresh
                                    System.Diagnostics.Debug.WriteLine($"Error syncing accounts after recovery: {ex.Message}");
                                }
                                
                                await Task.Delay(1500);
                                HostScreen.Router.NavigateBack.Execute();
                                return;
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Recovery mode: Existing item {_existingItemId} not found");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error during item recovery: {ex.Message}");
                    }
                    
                    // If the recovery logic fails, proceed with standard flow as fallback
                    System.Diagnostics.Debug.WriteLine($"Recovery mode: Falling back to standard flow");
                }
                
                // Exchange the public token for an access token, passing institution info
                var (accessToken, itemId) = await _plaidService.ExchangePublicTokenAsync(
                    publicToken,
                    institutionName);
                
                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(itemId))
                {
                    IsError = true;
                    ErrorMessage = "Failed to complete account linking. Please try again.";
                    System.Diagnostics.Debug.WriteLine("Token exchange failed: received null access token or item ID");
                    return;
                }
                
                // Get institution information to provide better user feedback
                string statusMessage = $"Successfully connected to {institutionName}.";
                
                // In recovery mode, provide different feedback
                if (FlowType == LinkFlowType.RecoverMode)
                {
                    statusMessage = $"Successfully recovered connection to {institutionName}.";
                }
                
                // Add more details for power users in development mode
                var env = await _plaidService.GetPlaidEnvironmentAsync();
                if (env?.ToLowerInvariant() == "sandbox" || env?.ToLowerInvariant() == "development")
                {
                    statusMessage += $" Item ID: {itemId.Substring(0, 8)}...";
                }
                
                // Show success state
                IsSuccess = true;
                StatusMessage = statusMessage + " Your accounts will appear shortly.";
                System.Diagnostics.Debug.WriteLine($"Plaid Link completed successfully. Item ID: {itemId}");
                
                // Sync accounts using the AccountSyncService if available
                try
                {
                    if (!string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(itemId) && _accountSyncService != null)
                    {
                        // Synchronize accounts to the local database
                        System.Diagnostics.Debug.WriteLine($"Synchronizing accounts for newly created item {itemId}");
                        var syncedAccounts = await _accountSyncService.SyncAccountsAsync(accessToken, itemId);
                        int accountCount = syncedAccounts.Count();
                        System.Diagnostics.Debug.WriteLine($"Synchronized {accountCount} accounts from {institutionName}");
                    }
                    else if (!string.IsNullOrEmpty(accessToken))
                    {
                        // If the sync service is not available, at least fetch and log
                        System.Diagnostics.Debug.WriteLine($"Fetching accounts for newly created item {itemId} (sync service not available)");
                        var accounts = await _plaidService.GetAccountsAsync(accessToken);
                        int accountCount = 0;
                        if (accounts != null) accountCount = accounts.ToList().Count;
                        System.Diagnostics.Debug.WriteLine($"Retrieved {accountCount} accounts from {institutionName}");
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't show error to user - this is just a background refresh
                    System.Diagnostics.Debug.WriteLine($"Error syncing accounts: {ex.Message}");
                }
                
                // Navigate back after a short delay to show the success message
                await Task.Delay(1500);
                HostScreen.Router.NavigateBack.Execute();
            }
            catch (Exception ex)
            {
                IsError = true;
                ErrorMessage = $"Failed to complete account linking: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Exception in HandlePlaidSuccessAsync: {ex}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Handles the exit of the Plaid Link flow without success
        /// </summary>
        public async Task HandlePlaidExitAsync(string error, string errorCode)
        {
            IsWebViewVisible = false;
            
            if (!string.IsNullOrEmpty(error))
            {
                IsError = true;
                System.Diagnostics.Debug.WriteLine($"Plaid Link Exit with error: {errorCode} - {error}");
                
                // Handle specific error codes with user-friendly messages
                ErrorMessage = errorCode switch
                {
                    "INSTITUTION_NOT_RESPONDING" => "The selected bank is not responding. Please try again later.",
                    "INSTITUTION_DOWN" => "The selected bank is currently unavailable. Please try again later.",
                    "INVALID_CREDENTIALS" => "The provided credentials were incorrect. Please try again.",
                    "RATE_LIMIT_EXCEEDED" => "Too many connection attempts. Please try again in a few minutes.",
                    "API_ERROR" => "There was a technical issue connecting to your bank. Please try again later.",
                    "CONNECTIVITY_ERROR" => "There was a problem connecting to your bank. Please check your internet connection and try again.",
                    "SESSION_TIMEOUT" => "Your session timed out. Please try connecting again.",
                    "OAUTH_ERROR" => "There was an authentication error. Please try again.",
                    "INSTITUTION_NO_LONGER_SUPPORTED" => "This financial institution is no longer supported. Please contact support.",
                    _ => $"Error connecting to bank: {error}"
                };
                
                // For recovery or update modes, offer specific advice
                if (FlowType == LinkFlowType.RecoverMode)
                {
                    StatusMessage = "Recovery attempt failed. You may need to remove and re-add this connection.";
                }
                else if (FlowType == LinkFlowType.UpdateMode)
                {
                    StatusMessage = "Update failed. Please try again later or contact support.";
                }
            }
            else
            {
                // User manually exited the flow
                System.Diagnostics.Debug.WriteLine("Plaid Link Exit: User cancelled the flow");
                StatusMessage = "Bank connection was cancelled.";
                await Task.Delay(1000);
                HostScreen.Router.NavigateBack.Execute();
            }
        }

        /// <summary>
        /// Cancels the linking process and navigates back
        /// </summary>
        private async Task CancelLinkingAsync()
        {
            IsWebViewVisible = false;
            StatusMessage = "Bank connection cancelled.";
            HostScreen.Router.NavigateBack.Execute();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Retries the linking process after an error
        /// </summary>
        private async Task RetryLinkingAsync()
        {
            ErrorMessage = string.Empty;
            IsError = false;
            await InitiatePlaidLinkAsync(forceRefresh: true);
        }
        
        /// <summary>
        /// Performs detailed diagnostics on Plaid configuration to help identify issues
        /// </summary>
        private async Task DiagnosePlaidConfiguration()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("================================");
                System.Diagnostics.Debug.WriteLine("PLAID CONFIGURATION DIAGNOSTICS");
                System.Diagnostics.Debug.WriteLine("================================");
                
                // Check assembly references
                System.Diagnostics.Debug.WriteLine("CHECKING REFERENCES:");
                try
                {
                    var currentAssembly = typeof(PlaidLinkViewModel).Assembly;
                    System.Diagnostics.Debug.WriteLine($"Current assembly: {currentAssembly.FullName}");
                    
                    var referencedAssemblies = currentAssembly.GetReferencedAssemblies();
                    System.Diagnostics.Debug.WriteLine($"Referenced assemblies count: {referencedAssemblies.Length}");
                    
                    var hasGoingPlaidRef = referencedAssemblies.Any(a => a.Name?.Contains("Going.Plaid") == true);
                    System.Diagnostics.Debug.WriteLine($"Has Going.Plaid reference: {hasGoingPlaidRef}");
                    
                    if (hasGoingPlaidRef)
                    {
                        var goingPlaidAssembly = referencedAssemblies.FirstOrDefault(a => a.Name?.Contains("Going.Plaid") == true);
                        System.Diagnostics.Debug.WriteLine($"Going.Plaid reference details: {goingPlaidAssembly?.FullName}");
                    }
                    
                    // Try to load Going.Plaid
                    try
                    {
                        var plaidType = typeof(Going.Plaid.PlaidClient);
                        System.Diagnostics.Debug.WriteLine($"PlaidClient type found: {plaidType.FullName}");
                        
                        var assembly = plaidType.Assembly;
                        System.Diagnostics.Debug.WriteLine($"Going.Plaid assembly loaded: {assembly.FullName}");
                        System.Diagnostics.Debug.WriteLine($"Going.Plaid version: {assembly.GetName().Version}");
                        
                        // Check if we can create instance
                        var envType = typeof(Going.Plaid.Environment);
                        System.Diagnostics.Debug.WriteLine($"Environment enum exists with values: {string.Join(", ", Enum.GetNames(envType))}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR: Cannot access Going.Plaid types: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR checking references: {ex.Message}");
                }
                
                // Check basic network connectivity
                System.Diagnostics.Debug.WriteLine("\nCHECKING NETWORK:");
                try
                {
                    using var client = new System.Net.Http.HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var pingResult = await client.GetAsync("https://sandbox.plaid.com/ping");
                    System.Diagnostics.Debug.WriteLine($"Plaid API reachable: {pingResult.IsSuccessStatusCode}, Status: {pingResult.StatusCode}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Network error connecting to Plaid: {ex.Message}");
                }
                
                // Get Plaid environment and configuration
                System.Diagnostics.Debug.WriteLine("\nCHECKING CONFIGURATION:");
                try
                {
                    var environment = await _plaidService.GetPlaidEnvironmentAsync() ?? "unknown";
                    System.Diagnostics.Debug.WriteLine($"Configured Plaid environment: {environment}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting Plaid environment: {ex.Message}");
                }
                
                System.Diagnostics.Debug.WriteLine("================================");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error running Plaid diagnostics: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Tests the Plaid credentials directly without using the PlaidService
        /// </summary>
        private async Task<bool> TestPlaidCredentials()
        {
            System.Diagnostics.Debug.WriteLine("Testing Plaid credentials directly...");
            
            // Get credentials from appsettings.Local.json
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Local.json", optional: true)
                .Build();
                
            var clientId = config["Plaid:ClientId"];
            var secret = config["Plaid:Secret"];
            var environment = config["Plaid:Environment"] ?? "sandbox";
            
            System.Diagnostics.Debug.WriteLine($"Loaded config - ClientId length: {clientId?.Length ?? 0}, Secret length: {secret?.Length ?? 0}, Environment: {environment}");
            
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
            {
                System.Diagnostics.Debug.WriteLine("Missing credentials in configuration");
                return false;
            }
            
            try
            {
                // Create a PlaidClient instance directly
                Going.Plaid.Environment env = Going.Plaid.Environment.Sandbox;
                if (environment.ToLowerInvariant() == "development")
                    env = Going.Plaid.Environment.Development;
                else if (environment.ToLowerInvariant() == "production")
                    env = Going.Plaid.Environment.Production;
                
                System.Diagnostics.Debug.WriteLine($"Creating Plaid client with Environment={env}, ClientId={clientId.Substring(0, Math.Min(5, clientId.Length))}...");
                
                var client = new Going.Plaid.PlaidClient(env, clientId, secret);
                System.Diagnostics.Debug.WriteLine("Successfully created PlaidClient");
                
                // Test with a simple API call
                var testRequest = new Going.Plaid.Sandbox.SandboxPublicTokenCreateRequest
                {
                    InstitutionId = "ins_109508", 
                    InitialProducts = new[] { Going.Plaid.Entity.Products.Auth }
                };
                
                System.Diagnostics.Debug.WriteLine("Sending test API request...");
                var response = await client.SandboxPublicTokenCreateAsync(testRequest);
                
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("Plaid API test successful!");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Plaid API test failed: {response.StatusCode} - {response.Error?.ErrorMessage}");
                    if (response.Error != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error details: Code={response.Error.ErrorCode}, Type={response.Error.ErrorType}, Display={response.Error.DisplayMessage}");
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Exception testing Plaid credentials: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }
    }
}