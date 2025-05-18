using Going.Plaid;
using Going.Plaid.Entity;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Going.Plaid.Accounts;
using Going.Plaid.Transactions;
using Going.Plaid.Institutions;
using Going.Plaid.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Core.Interfaces;
using MyApp.Core.Models.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoreAccount = MyApp.Core.Entities.Account;
using CoreTransaction = MyApp.Core.Entities.Transaction;
using CoreTransactionType = MyApp.Core.Entities.TransactionType;

namespace MyApp.Infrastructure.Services
{
    /// <summary>
    /// Simple implementation of Plaid Service
    /// </summary>
    public class PlaidService : IPlaidService
    {
        private readonly ILogger<PlaidService> _logger;
        private readonly PlaidOptions _plaidOptions;
        private readonly IPlaidTokenManager _tokenManager;
        private readonly IAuditLogger _auditLogger;
        private readonly PlaidClient? _plaidClient;
        private bool _isInitialized = false;

        public PlaidService(
            IOptions<PlaidOptions> plaidOptions, 
            IPlaidTokenManager tokenManager,
            IAuditLogger auditLogger,
            ILogger<PlaidService> logger)
        {
            _logger = logger;
            _plaidOptions = plaidOptions.Value;
            _tokenManager = tokenManager;
            _auditLogger = auditLogger;

            _logger.LogInformation("Initializing PlaidService");
            _logger.LogInformation("Environment: {Environment}, ClientId length: {ClientIdLength}, Secret length: {SecretLength}",
                _plaidOptions.Environment,
                _plaidOptions.ClientId?.Length ?? 0,
                _plaidOptions.Secret?.Length ?? 0);

            try
            {
                // Get the environment enum
                Environment env;
                switch (_plaidOptions.Environment.ToLowerInvariant())
                {
                    case "sandbox": env = Environment.Sandbox; break;
                    case "development": env = Environment.Development; break;
                    case "production": env = Environment.Production; break;
                    default: throw new ArgumentException($"Invalid environment: {_plaidOptions.Environment}");
                }

                // Create Plaid client
                _plaidClient = new PlaidClient(env, _plaidOptions.ClientId, _plaidOptions.Secret);
                
                _logger.LogInformation("PlaidClient created successfully");
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PlaidService");
                _isInitialized = false;
            }
        }

        public async Task<bool> IsConfiguredCorrectlyAsync()
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return false;
            }

            try
            {
                var request = new LinkTokenCreateRequest
                {
                    ClientName = "Test Client",
                    Language = Language.English,
                    CountryCodes = new[] { CountryCode.Us },
                    User = new LinkTokenCreateRequestUser { ClientUserId = "test_user" },
                    Products = new[] { Products.Auth }
                };

                var response = await _plaidClient.LinkTokenCreateAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing Plaid configuration");
                return false;
            }
        }

        public Task<string?> GetPlaidEnvironmentAsync()
        {
            return Task.FromResult<string?>(_plaidOptions.Environment);
        }

        public async Task<string?> CreateLinkTokenAsync(string userId)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return null;
            }

            try
            {
                var request = new LinkTokenCreateRequest
                {
                    ClientName = _plaidOptions.ClientName ?? "MyApp",
                    Language = Language.English,
                    CountryCodes = new[] { CountryCode.Us },
                    User = new LinkTokenCreateRequestUser { ClientUserId = userId },
                    Products = new[] { Products.Auth, Products.Transactions }
                };

                _logger.LogInformation("Creating link token for user {UserId}", userId);
                var response = await _plaidClient.LinkTokenCreateAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Link token created successfully");
                    return response.LinkToken;
                }
                else
                {
                    _logger.LogError("Failed to create link token: {ErrorMessage}", 
                        response.Error?.ErrorMessage ?? "No error message");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating link token");
                return null;
            }
        }

        public async Task<(string? AccessToken, string? ItemId)> ExchangePublicTokenAsync(
            string publicToken, 
            string? institutionName = null, 
            string? institutionId = null)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return (null, null);
            }

            try
            {
                var request = new ItemPublicTokenExchangeRequest
                {
                    PublicToken = publicToken
                };

                _logger.LogInformation("Exchanging public token");
                var response = await _plaidClient.ItemPublicTokenExchangeAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Public token exchanged successfully");
                    
                    if (_tokenManager != null)
                    {
                        await _tokenManager.StoreAccessTokenAsync(
                            response.ItemId,
                            response.AccessToken,
                            institutionId,
                            institutionName ?? "Connected Bank");
                    }
                    
                    return (response.AccessToken, response.ItemId);
                }
                else
                {
                    _logger.LogError("Failed to exchange public token: {ErrorMessage}", 
                        response.Error?.ErrorMessage ?? "No error message");
                    return (null, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exchanging public token");
                return (null, null);
            }
        }

        public async Task<IEnumerable<CoreAccount>> GetAccountsAsync(string accessToken)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return Enumerable.Empty<CoreAccount>();
            }

            try
            {
                var request = new AccountsGetRequest
                {
                    AccessToken = accessToken
                };

                _logger.LogInformation("Getting accounts");
                var response = await _plaidClient.AccountsGetAsync(request);

                if (response.IsSuccessStatusCode && response.Accounts != null)
                {
                    _logger.LogInformation("Retrieved {Count} accounts", response.Accounts.Count);
                    
                    return response.Accounts.Select(a => new CoreAccount
                    {
                        PlaidAccountId = a.AccountId,
                        PlaidItemId = response.Item?.ItemId,
                        Name = a.Name ?? "Unnamed Account",
                        Type = a.Type.ToString(),
                        Balance = a.Balances?.Current ?? 0m,
                        BankName = response.Item?.InstitutionId
                    });
                }
                else
                {
                    _logger.LogError("Failed to get accounts: {ErrorMessage}", 
                        response.Error?.ErrorMessage ?? "No error message");
                    return Enumerable.Empty<CoreAccount>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting accounts");
                return Enumerable.Empty<CoreAccount>();
            }
        }

        public async Task<IEnumerable<CoreTransaction>> GetTransactionsAsync(string accessToken, DateTime startDate, DateTime endDate)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return Enumerable.Empty<CoreTransaction>();
            }

            try
            {
                var request = new TransactionsSyncRequest
                {
                    AccessToken = accessToken
                };

                _logger.LogInformation("Getting transactions for {StartDate} to {EndDate}", startDate, endDate);
                var response = await _plaidClient.TransactionsSyncAsync(request);

                if (response.IsSuccessStatusCode && response.Added != null)
                {
                    _logger.LogInformation("Retrieved {Count} transactions", response.Added.Count);
                    
                    var startDateOnly = DateOnly.FromDateTime(startDate);
                    var endDateOnly = DateOnly.FromDateTime(endDate);
                    
                    var result = new List<CoreTransaction>();
                    
                    foreach (var tx in response.Added)
                    {
                        if (tx.Date != null && tx.Date.Value >= startDateOnly && tx.Date.Value <= endDateOnly)
                        {
                            result.Add(new CoreTransaction
                            {
                                PlaidTransactionId = tx.TransactionId,
                                Date = tx.Date?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Now,
                                Amount = tx.Amount ?? 0m,
                                Payee = tx.MerchantName ?? tx.Name ?? "Unknown",
                                Description = tx.Name,
                                Type = (tx.Amount ?? 0) < 0 ? CoreTransactionType.Expense : CoreTransactionType.Income,
                                IsCleared = true
                            });
                        }
                    }
                    
                    return result;
                }
                else
                {
                    _logger.LogError("Failed to get transactions: {ErrorMessage}", 
                        response.Error?.ErrorMessage ?? "No error message");
                    return Enumerable.Empty<CoreTransaction>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting transactions");
                return Enumerable.Empty<CoreTransaction>();
            }
        }

        public async Task<bool> UpdateItemAsync(string accessToken)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return false;
            }

            try
            {
                var request = new ItemGetRequest
                {
                    AccessToken = accessToken
                };

                _logger.LogInformation("Updating item");
                var response = await _plaidClient.ItemGetAsync(request);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating item");
                return false;
            }
        }

        public async Task<bool> RemoveItemAsync(string accessToken)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return false;
            }

            try
            {
                var request = new ItemRemoveRequest
                {
                    AccessToken = accessToken
                };

                _logger.LogInformation("Removing item");
                var response = await _plaidClient.ItemRemoveAsync(request);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing item");
                return false;
            }
        }

        public async Task<string?> GetItemStatusAsync(string accessToken)
        {
            if (!_isInitialized || _plaidClient == null)
            {
                _logger.LogError("PlaidService not initialized");
                return null;
            }

            try
            {
                var request = new ItemGetRequest
                {
                    AccessToken = accessToken
                };

                _logger.LogInformation("Getting item status");
                var response = await _plaidClient.ItemGetAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Item status retrieved successfully");
                    
                    return $"Institution ID: {response.Item?.InstitutionId}, " +
                           $"Available Products: {string.Join(",", response.Item?.AvailableProducts?.Select(p => p.ToString()) ?? Enumerable.Empty<string>())}";
                }
                else
                {
                    _logger.LogError("Failed to get item status: {ErrorMessage}", 
                        response.Error?.ErrorMessage ?? "No error message");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting item status");
                return null;
            }
        }
    }
} 