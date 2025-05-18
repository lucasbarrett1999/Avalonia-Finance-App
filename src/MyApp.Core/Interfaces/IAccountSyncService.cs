using System.Collections.Generic;
using System.Threading.Tasks;
using MyApp.Core.Entities;

namespace MyApp.Core.Interfaces
{
    /// <summary>
    /// Service for synchronizing account data between Plaid and the local database
    /// </summary>
    public interface IAccountSyncService
    {
        /// <summary>
        /// Synchronizes accounts for a specific Plaid Item
        /// </summary>
        /// <param name="accessToken">The Plaid access token</param>
        /// <param name="plaidItemId">The Plaid Item ID</param>
        /// <returns>The synchronized accounts</returns>
        Task<IEnumerable<Account>> SyncAccountsAsync(string accessToken, string plaidItemId);
        
        /// <summary>
        /// Gets all accounts for a specific Plaid Item
        /// </summary>
        /// <param name="plaidItemId">The Plaid Item ID</param>
        /// <returns>The accounts associated with the Plaid Item</returns>
        Task<IEnumerable<Account>> GetAccountsByPlaidItemIdAsync(string plaidItemId);
        
        /// <summary>
        /// Gets all accounts stored in the local database
        /// </summary>
        /// <returns>All accounts</returns>
        Task<IEnumerable<Account>> GetAllAccountsAsync();
    }
}