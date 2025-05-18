using MyApp.Core.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyApp.Core.Interfaces
{
    public interface IAccountRepository : IRepository<Account>
    {
        Task<Account?> GetAccountByNameAsync(string name);
        
        // Plaid specific account methods
        Task<Account?> GetAccountByPlaidAccountIdAsync(string plaidAccountId);
        Task<IEnumerable<Account>> GetAccountsByPlaidItemIdAsync(string plaidItemId);
        Task<Account> AddOrUpdatePlaidAccountAsync(Account account);
        Task<int> SyncPlaidAccountsAsync(IEnumerable<Account> accounts, string plaidItemId);
    }
} 