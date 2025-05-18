using System;
using System.Threading.Tasks;
using MyApp.Core.Entities;

namespace MyApp.Core.Interfaces
{
    /// <summary>
    /// Manages Plaid access tokens securely
    /// </summary>
    public interface IPlaidTokenManager
    {
        /// <summary>
        /// Stores a new Plaid access token
        /// </summary>
        /// <param name="plaidItemId">The Plaid item ID</param>
        /// <param name="accessToken">The access token to store</param>
        /// <param name="institutionId">Optional institution ID</param>
        /// <param name="institutionName">Optional institution name</param>
        /// <returns>The created PlaidItem</returns>
        Task<PlaidItem> StoreAccessTokenAsync(
            string plaidItemId, 
            string accessToken, 
            string? institutionId = null, 
            string? institutionName = null);
        
        /// <summary>
        /// Gets an access token by Plaid item ID
        /// </summary>
        /// <param name="plaidItemId">The Plaid item ID</param>
        /// <returns>The decrypted access token, or null if not found</returns>
        Task<string?> GetAccessTokenAsync(string plaidItemId);
        
        /// <summary>
        /// Gets a PlaidItem by its Plaid-specific item ID
        /// </summary>
        /// <param name="plaidItemId">The Plaid item ID</param>
        /// <returns>The PlaidItem or null if not found</returns>
        Task<PlaidItem?> GetPlaidItemAsync(string plaidItemId);
        
        /// <summary>
        /// Gets a PlaidItem by its internal ID
        /// </summary>
        /// <param name="id">The internal database ID</param>
        /// <returns>The PlaidItem or null if not found</returns>
        Task<PlaidItem?> GetPlaidItemByIdAsync(int id);
        
        /// <summary>
        /// Updates a Plaid item's metadata
        /// </summary>
        Task UpdatePlaidItemAsync(
            int id,
            string? institutionId = null, 
            string? institutionName = null,
            string? availableProducts = null);
        
        /// <summary>
        /// Records an error on a Plaid item
        /// </summary>
        Task RecordItemErrorAsync(int id, string errorCode, string errorMessage);
        
        /// <summary>
        /// Clears error state from a Plaid item
        /// </summary>
        Task ClearItemErrorAsync(int id);
        
        /// <summary>
        /// Records that a Plaid item was accessed
        /// </summary>
        Task RecordItemAccessAsync(int id);
        
        /// <summary>
        /// Schedules the next refresh for a Plaid item
        /// </summary>
        Task ScheduleRefreshAsync(int id, TimeSpan? refreshInterval = null);
        
        /// <summary>
        /// Removes a Plaid item and its access token
        /// </summary>
        Task RemovePlaidItemAsync(int id);
        
        /// <summary>
        /// Rotates an access token (replaces it with a new one)
        /// </summary>
        Task RotateAccessTokenAsync(int id, string newAccessToken);
    }
}