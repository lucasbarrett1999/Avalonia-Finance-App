using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MyApp.Core.Entities;
using MyApp.Core.Interfaces;

namespace MyApp.Infrastructure.Services
{
    public class AccountSyncService : IAccountSyncService
    {
        private readonly IPlaidService _plaidService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<AccountSyncService> _logger;

        public AccountSyncService(
            IPlaidService plaidService,
            IUnitOfWork unitOfWork,
            ILogger<AccountSyncService> logger)
        {
            _plaidService = plaidService ?? throw new ArgumentNullException(nameof(plaidService));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Synchronizes accounts for a specific Plaid Item
        /// </summary>
        public async Task<IEnumerable<Account>> SyncAccountsAsync(string accessToken, string plaidItemId)
        {
            try
            {
                if (string.IsNullOrEmpty(accessToken))
                    throw new ArgumentException("Access token is required", nameof(accessToken));
                    
                if (string.IsNullOrEmpty(plaidItemId))
                    throw new ArgumentException("Plaid Item ID is required", nameof(plaidItemId));
                    
                // Fetch accounts from Plaid
                _logger.LogInformation("Fetching accounts from Plaid for item {PlaidItemId}", plaidItemId);
                var plaidAccounts = await _plaidService.GetAccountsAsync(accessToken);
                
                if (!plaidAccounts.Any())
                {
                    _logger.LogWarning("No accounts found in Plaid for item {PlaidItemId}", plaidItemId);
                    return Enumerable.Empty<Account>();
                }
                
                // Sync accounts with local database
                _logger.LogInformation("Syncing {Count} accounts from Plaid for item {PlaidItemId}", 
                    plaidAccounts.Count(), plaidItemId);
                int syncedCount = await _unitOfWork.AccountRepository.SyncPlaidAccountsAsync(plaidAccounts, plaidItemId);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogInformation("Successfully synced {Count} accounts for item {PlaidItemId}", 
                    syncedCount, plaidItemId);
                
                // Return the updated accounts from the database
                return await _unitOfWork.AccountRepository.GetAccountsByPlaidItemIdAsync(plaidItemId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing accounts for Plaid item {PlaidItemId}", plaidItemId);
                throw;
            }
        }

        /// <summary>
        /// Gets all accounts for a specific Plaid Item
        /// </summary>
        public async Task<IEnumerable<Account>> GetAccountsByPlaidItemIdAsync(string plaidItemId)
        {
            if (string.IsNullOrEmpty(plaidItemId))
                throw new ArgumentException("Plaid Item ID is required", nameof(plaidItemId));
                
            return await _unitOfWork.AccountRepository.GetAccountsByPlaidItemIdAsync(plaidItemId);
        }

        /// <summary>
        /// Gets all accounts stored in the local database
        /// </summary>
        public async Task<IEnumerable<Account>> GetAllAccountsAsync()
        {
            return await _unitOfWork.AccountRepository.GetAllAsync();
        }
    }
}