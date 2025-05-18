using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Core.Entities;
using MyApp.Core.Interfaces;
using MyApp.Core.Models.Configuration;

namespace MyApp.Infrastructure.Services
{
    /// <summary>
    /// Manages secure storage and retrieval of Plaid access tokens
    /// </summary>
    public class PlaidTokenManager : IPlaidTokenManager
    {
        private readonly IPlaidItemRepository _plaidItemRepository;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<PlaidTokenManager> _logger;
        private readonly IUnitOfWork _unitOfWork;
        private readonly PlaidOptions _plaidOptions;

        public PlaidTokenManager(
            IPlaidItemRepository plaidItemRepository,
            IEncryptionService encryptionService,
            IUnitOfWork unitOfWork,
            IOptions<PlaidOptions> plaidOptions,
            ILogger<PlaidTokenManager> logger)
        {
            _plaidItemRepository = plaidItemRepository ?? throw new ArgumentNullException(nameof(plaidItemRepository));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plaidOptions = plaidOptions?.Value ?? throw new ArgumentNullException(nameof(plaidOptions));
        }

        /// <summary>
        /// Stores a new Plaid access token
        /// </summary>
        public async Task<PlaidItem> StoreAccessTokenAsync(
            string plaidItemId, 
            string accessToken, 
            string? institutionId = null, 
            string? institutionName = null)
        {
            if (string.IsNullOrWhiteSpace(plaidItemId))
                throw new ArgumentException("Plaid item ID is required", nameof(plaidItemId));
            
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token is required", nameof(accessToken));

            try
            {
                // Check if item already exists
                var existingItem = await _plaidItemRepository.GetByPlaidItemIdAsync(plaidItemId);
                
                if (existingItem != null)
                {
                    _logger.LogWarning("Attempted to store access token for existing Plaid item: {PlaidItemId}", plaidItemId);
                    // Return existing item but don't update the token
                    return existingItem;
                }

                // Encrypt the access token
                var encryptedToken = _encryptionService.Encrypt(accessToken);
                
                // Create the new PlaidItem
                var plaidItem = new PlaidItem
                {
                    ItemId = plaidItemId,
                    AccessToken = encryptedToken,
                    InstitutionId = institutionId,
                    InstitutionName = institutionName,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastAccessedAt = DateTime.UtcNow,
                    NextRefreshScheduledAt = CalculateNextRefreshTime(),
                    IsActive = true
                };

                // Add to the repository
                await _plaidItemRepository.AddAsync(plaidItem);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogInformation("Stored new access token for Plaid item: {PlaidItemId}", plaidItemId);
                
                return plaidItem;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing access token for Plaid item: {PlaidItemId}", plaidItemId);
                throw;
            }
        }

        /// <summary>
        /// Gets an access token by Plaid item ID
        /// </summary>
        public async Task<string?> GetAccessTokenAsync(string plaidItemId)
        {
            if (string.IsNullOrWhiteSpace(plaidItemId))
            {
                _logger.LogWarning("Attempted to get access token with empty Plaid item ID");
                return null;
            }

            try
            {
                var plaidItem = await _plaidItemRepository.GetByPlaidItemIdAsync(plaidItemId);
                
                if (plaidItem == null)
                {
                    _logger.LogWarning("No Plaid item found for ID: {PlaidItemId}", plaidItemId);
                    return null;
                }

                if (!plaidItem.IsActive)
                {
                    _logger.LogWarning("Attempted to get access token for inactive Plaid item: {PlaidItemId}", plaidItemId);
                    return null;
                }

                // Record the access
                await RecordItemAccessAsync(plaidItem.Id);
                
                // Decrypt and return the token
                return _encryptionService.Decrypt(plaidItem.AccessToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting access token for Plaid item: {PlaidItemId}", plaidItemId);
                return null;
            }
        }

        /// <summary>
        /// Gets a PlaidItem by its Plaid-specific item ID
        /// </summary>
        public async Task<PlaidItem?> GetPlaidItemAsync(string plaidItemId)
        {
            if (string.IsNullOrWhiteSpace(plaidItemId))
                return null;

            try
            {
                return await _plaidItemRepository.GetByPlaidItemIdAsync(plaidItemId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Plaid item: {PlaidItemId}", plaidItemId);
                return null;
            }
        }

        /// <summary>
        /// Gets a PlaidItem by its internal ID
        /// </summary>
        public async Task<PlaidItem?> GetPlaidItemByIdAsync(int id)
        {
            try
            {
                return await _plaidItemRepository.GetByIdAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Plaid item by ID: {Id}", id);
                return null;
            }
        }

        /// <summary>
        /// Updates a Plaid item's metadata
        /// </summary>
        public async Task UpdatePlaidItemAsync(
            int id,
            string? institutionId = null, 
            string? institutionName = null,
            string? availableProducts = null)
        {
            try
            {
                var plaidItem = await _plaidItemRepository.GetByIdAsync(id);
                
                if (plaidItem == null)
                {
                    _logger.LogWarning("Cannot update non-existent Plaid item: {Id}", id);
                    return;
                }

                bool updated = false;
                
                if (institutionId != null && institutionId != plaidItem.InstitutionId)
                {
                    plaidItem.InstitutionId = institutionId;
                    updated = true;
                }
                
                if (institutionName != null && institutionName != plaidItem.InstitutionName)
                {
                    plaidItem.InstitutionName = institutionName;
                    updated = true;
                }
                
                if (availableProducts != null && availableProducts != plaidItem.AvailableProducts)
                {
                    plaidItem.AvailableProducts = availableProducts;
                    updated = true;
                }

                if (updated)
                {
                    plaidItem.UpdatedAt = DateTime.UtcNow;
                    await _plaidItemRepository.UpdateAsync(plaidItem);
                    await _unitOfWork.CompleteAsync();
                    
                    _logger.LogInformation("Updated metadata for Plaid item: {Id}", id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Plaid item: {Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Records an error on a Plaid item
        /// </summary>
        public async Task RecordItemErrorAsync(int id, string errorCode, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
                throw new ArgumentException("Error code is required", nameof(errorCode));
            
            if (string.IsNullOrWhiteSpace(errorMessage))
                throw new ArgumentException("Error message is required", nameof(errorMessage));

            try
            {
                await _plaidItemRepository.UpdateItemErrorAsync(id, errorCode, errorMessage);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogWarning("Recorded error for Plaid item {Id}: {ErrorCode} - {ErrorMessage}", 
                    id, errorCode, errorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording item error for Plaid item: {Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Clears error state from a Plaid item
        /// </summary>
        public async Task ClearItemErrorAsync(int id)
        {
            try
            {
                await _plaidItemRepository.ClearItemErrorAsync(id);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogInformation("Cleared error state for Plaid item: {Id}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing item error for Plaid item: {Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Records that a Plaid item was accessed
        /// </summary>
        public async Task RecordItemAccessAsync(int id)
        {
            try
            {
                await _plaidItemRepository.UpdateLastAccessedTimeAsync(id);
                await _unitOfWork.CompleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording access for Plaid item: {Id}", id);
                // Don't rethrow - this is a non-critical operation
            }
        }

        /// <summary>
        /// Schedules the next refresh for a Plaid item
        /// </summary>
        public async Task ScheduleRefreshAsync(int id, TimeSpan? refreshInterval = null)
        {
            try
            {
                // Use provided interval or default from configuration
                var interval = refreshInterval ?? TimeSpan.FromHours(_plaidOptions.Features.RefreshIntervalHours);
                
                // Calculate next refresh time
                var nextRefresh = DateTime.UtcNow.Add(interval);
                
                await _plaidItemRepository.ScheduleNextRefreshAsync(id, nextRefresh);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogInformation("Scheduled next refresh for Plaid item {Id} at {NextRefresh}", 
                    id, nextRefresh);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling refresh for Plaid item: {Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Removes a Plaid item and its access token
        /// </summary>
        public async Task RemovePlaidItemAsync(int id)
        {
            try
            {
                // Use soft delete to maintain audit trail
                await _plaidItemRepository.SoftDeleteAsync(id);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogInformation("Soft-deleted Plaid item: {Id}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing Plaid item: {Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Rotates an access token (replaces it with a new one)
        /// </summary>
        public async Task RotateAccessTokenAsync(int id, string newAccessToken)
        {
            if (string.IsNullOrWhiteSpace(newAccessToken))
                throw new ArgumentException("New access token is required", nameof(newAccessToken));

            try
            {
                var plaidItem = await _plaidItemRepository.GetByIdAsync(id);
                
                if (plaidItem == null)
                {
                    _logger.LogWarning("Cannot rotate token for non-existent Plaid item: {Id}", id);
                    return;
                }

                // Encrypt the new token
                var encryptedToken = _encryptionService.Encrypt(newAccessToken);
                
                // Update the item
                plaidItem.AccessToken = encryptedToken;
                plaidItem.UpdatedAt = DateTime.UtcNow;
                plaidItem.HasError = false;
                plaidItem.ErrorCode = null;
                plaidItem.ErrorMessage = null;
                
                await _plaidItemRepository.UpdateAsync(plaidItem);
                await _unitOfWork.CompleteAsync();
                
                _logger.LogInformation("Rotated access token for Plaid item: {Id}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rotating access token for Plaid item: {Id}", id);
                throw;
            }
        }

        /// <summary>
        /// Calculates the next refresh time based on configuration
        /// </summary>
        private DateTime CalculateNextRefreshTime()
        {
            if (!_plaidOptions.Features.EnableRefreshInterval)
            {
                // If refresh is disabled, set a far future date
                return DateTime.UtcNow.AddYears(10);
            }
            
            return DateTime.UtcNow.AddHours(_plaidOptions.Features.RefreshIntervalHours);
        }
    }
}