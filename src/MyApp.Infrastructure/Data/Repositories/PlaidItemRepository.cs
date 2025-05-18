using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MyApp.Core.Entities;
using MyApp.Core.Interfaces;

namespace MyApp.Infrastructure.Data.Repositories
{
    /// <summary>
    /// Repository implementation for Plaid Items
    /// </summary>
    public class PlaidItemRepository : Repository<PlaidItem>, IPlaidItemRepository
    {
        public PlaidItemRepository(FinanceDbContext context) : base(context)
        {
        }

        private FinanceDbContext DbContext => _context as FinanceDbContext;

        /// <summary>
        /// Gets a Plaid item by its Plaid-specific item ID
        /// </summary>
        public async Task<PlaidItem?> GetByPlaidItemIdAsync(string plaidItemId)
        {
            if (string.IsNullOrWhiteSpace(plaidItemId))
                return null;

            return await DbContext.PlaidItems
                .Include(i => i.Accounts)
                .FirstOrDefaultAsync(i => i.ItemId == plaidItemId && i.IsActive);
        }

        /// <summary>
        /// Gets all active Plaid items
        /// </summary>
        public async Task<IEnumerable<PlaidItem>> GetActiveItemsAsync()
        {
            return await DbContext.PlaidItems
                .Where(i => i.IsActive)
                .OrderByDescending(i => i.UpdatedAt)
                .ToListAsync();
        }

        /// <summary>
        /// Gets all Plaid items that need to be refreshed
        /// </summary>
        public async Task<IEnumerable<PlaidItem>> GetItemsDueForRefreshAsync()
        {
            var now = DateTime.UtcNow;
            
            return await DbContext.PlaidItems
                .Where(i => i.IsActive && 
                           !i.HasError && 
                           i.NextRefreshScheduledAt <= now)
                .OrderBy(i => i.NextRefreshScheduledAt)
                .ToListAsync();
        }

        /// <summary>
        /// Gets all Plaid items with errors
        /// </summary>
        public async Task<IEnumerable<PlaidItem>> GetItemsWithErrorsAsync()
        {
            return await DbContext.PlaidItems
                .Where(i => i.IsActive && i.HasError)
                .OrderByDescending(i => i.UpdatedAt)
                .ToListAsync();
        }

        /// <summary>
        /// Updates the last accessed time for an item
        /// </summary>
        public async Task UpdateLastAccessedTimeAsync(int itemId)
        {
            var item = await DbContext.PlaidItems.FindAsync(itemId);
            if (item != null)
            {
                item.LastAccessedAt = DateTime.UtcNow;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Updates an item with error information
        /// </summary>
        public async Task UpdateItemErrorAsync(int itemId, string errorCode, string errorMessage)
        {
            var item = await DbContext.PlaidItems.FindAsync(itemId);
            if (item != null)
            {
                item.HasError = true;
                item.ErrorCode = errorCode;
                item.ErrorMessage = errorMessage;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Clears error information from an item
        /// </summary>
        public async Task ClearItemErrorAsync(int itemId)
        {
            var item = await DbContext.PlaidItems.FindAsync(itemId);
            if (item != null)
            {
                item.HasError = false;
                item.ErrorCode = null;
                item.ErrorMessage = null;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Schedules the next refresh time for an item
        /// </summary>
        public async Task ScheduleNextRefreshAsync(int itemId, DateTime nextRefreshTime)
        {
            var item = await DbContext.PlaidItems.FindAsync(itemId);
            if (item != null)
            {
                item.NextRefreshScheduledAt = nextRefreshTime;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Soft-deletes a Plaid item by marking it as inactive
        /// </summary>
        public async Task SoftDeleteAsync(int itemId)
        {
            var item = await DbContext.PlaidItems.FindAsync(itemId);
            if (item != null)
            {
                item.IsActive = false;
                item.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}