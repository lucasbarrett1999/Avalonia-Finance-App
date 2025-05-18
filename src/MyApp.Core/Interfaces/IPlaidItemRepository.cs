using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MyApp.Core.Entities;

namespace MyApp.Core.Interfaces
{
    /// <summary>
    /// Repository for managing Plaid Items
    /// </summary>
    public interface IPlaidItemRepository : IRepository<PlaidItem>
    {
        /// <summary>
        /// Gets a Plaid item by its Plaid-specific item ID
        /// </summary>
        Task<PlaidItem?> GetByPlaidItemIdAsync(string plaidItemId);
        
        /// <summary>
        /// Gets all active Plaid items
        /// </summary>
        Task<IEnumerable<PlaidItem>> GetActiveItemsAsync();
        
        /// <summary>
        /// Gets all Plaid items that need to be refreshed
        /// </summary>
        Task<IEnumerable<PlaidItem>> GetItemsDueForRefreshAsync();
        
        /// <summary>
        /// Gets all Plaid items with errors
        /// </summary>
        Task<IEnumerable<PlaidItem>> GetItemsWithErrorsAsync();
        
        /// <summary>
        /// Updates the last accessed time for an item
        /// </summary>
        Task UpdateLastAccessedTimeAsync(int itemId);
        
        /// <summary>
        /// Updates an item with error information
        /// </summary>
        Task UpdateItemErrorAsync(int itemId, string errorCode, string errorMessage);
        
        /// <summary>
        /// Clears error information from an item
        /// </summary>
        Task ClearItemErrorAsync(int itemId);
        
        /// <summary>
        /// Schedules the next refresh time for an item
        /// </summary>
        Task ScheduleNextRefreshAsync(int itemId, DateTime nextRefreshTime);
        
        /// <summary>
        /// Soft-deletes a Plaid item by marking it as inactive
        /// </summary>
        Task SoftDeleteAsync(int itemId);
    }
}