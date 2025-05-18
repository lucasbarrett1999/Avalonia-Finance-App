using System.Collections.Generic;
using System.Threading.Tasks;
using MyApp.Core.Entities;

namespace MyApp.Core.Interfaces
{
    public interface IPlaidService
    {
        Task<bool> IsConfiguredCorrectlyAsync();
        Task<string?> GetPlaidEnvironmentAsync();
        
        // Link token creation and exchange
        Task<string?> CreateLinkTokenAsync(string userId);
        Task<(string? AccessToken, string? ItemId)> ExchangePublicTokenAsync(
            string publicToken, 
            string? institutionName = null, 
            string? institutionId = null);
        
        // Account information
        Task<IEnumerable<Account>> GetAccountsAsync(string accessToken);
        
        // Transaction data
        Task<IEnumerable<Transaction>> GetTransactionsAsync(string accessToken, System.DateTime startDate, System.DateTime endDate);
        
        // Item management
        Task<bool> UpdateItemAsync(string accessToken);
        Task<bool> RemoveItemAsync(string accessToken);
        
        // Error handling and status
        Task<string?> GetItemStatusAsync(string accessToken);
    }
} 