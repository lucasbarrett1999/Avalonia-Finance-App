using Microsoft.EntityFrameworkCore;
using MyApp.Core.Entities;
using MyApp.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyApp.Infrastructure.Data.Repositories
{
    public class AccountRepository : Repository<Account>, IAccountRepository
    {
        public AccountRepository(FinanceDbContext context) : base(context)
        {
        }

        public async Task<Account?> GetAccountByNameAsync(string name)
        {
            // Access _dbSet (which is DbSet<Account>) from the base Repository<T> class
            return await _dbSet.FirstOrDefaultAsync(a => a.Name == name);
        }

        /// <summary>
        /// Gets an account by its Plaid account ID
        /// </summary>
        public async Task<Account?> GetAccountByPlaidAccountIdAsync(string plaidAccountId)
        {
            if (string.IsNullOrEmpty(plaidAccountId))
                return null;
                
            return await _dbSet.FirstOrDefaultAsync(a => a.PlaidAccountId == plaidAccountId);
        }

        /// <summary>
        /// Gets all accounts associated with a specific Plaid item ID
        /// </summary>
        public async Task<IEnumerable<Account>> GetAccountsByPlaidItemIdAsync(string plaidItemId)
        {
            if (string.IsNullOrEmpty(plaidItemId))
                return Enumerable.Empty<Account>();
                
            return await _dbSet.Where(a => a.PlaidItemId == plaidItemId)
                .OrderBy(a => a.Name)
                .ToListAsync();
        }

        /// <summary>
        /// Adds a new Plaid account or updates an existing one
        /// </summary>
        public async Task<Account> AddOrUpdatePlaidAccountAsync(Account account)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));
                
            if (string.IsNullOrEmpty(account.PlaidAccountId))
                throw new ArgumentException("Account must have a PlaidAccountId", nameof(account));
                
            // Check if this account already exists
            var existingAccount = await GetAccountByPlaidAccountIdAsync(account.PlaidAccountId);
            
            if (existingAccount != null)
            {
                // Update existing account
                existingAccount.Name = account.Name;
                existingAccount.Type = account.Type;
                existingAccount.Balance = account.Balance;
                existingAccount.BankName = account.BankName;
                
                // Don't change the PlaidAccountId, it's used as the lookup key
                
                // Update doesn't modify PlaidItemId either, as it shouldn't change
                
                await UpdateAsync(existingAccount);
                return existingAccount;
            }
            else
            {
                // Add new account
                await AddAsync(account);
                return account;
            }
        }

        /// <summary>
        /// Synchronizes a list of Plaid accounts with the database
        /// This is the main method for updating accounts after a Plaid refresh
        /// </summary>
        /// <returns>The number of accounts synchronized</returns>
        public async Task<int> SyncPlaidAccountsAsync(IEnumerable<Account> accounts, string plaidItemId)
        {
            if (accounts == null)
                throw new ArgumentNullException(nameof(accounts));
                
            if (string.IsNullOrEmpty(plaidItemId))
                throw new ArgumentException("A valid Plaid item ID is required", nameof(plaidItemId));
                
            int count = 0;
            
            foreach (var account in accounts)
            {
                // Ensure the account has the correct Plaid item ID
                account.PlaidItemId = plaidItemId;
                
                // Ensure the account has a Plaid account ID
                if (string.IsNullOrEmpty(account.PlaidAccountId))
                {
                    // Skip accounts without a valid Plaid account ID
                    continue;
                }
                
                // Add or update the account
                await AddOrUpdatePlaidAccountAsync(account);
                count++;
            }
            
            return count;
        }

        /// <summary>
        /// Gets an account with its associated transactions
        /// </summary>
        public async Task<Account?> GetAccountWithTransactionsAsync(int id)
        {
            return await _dbSet.Include(a => a.Transactions)
                .FirstOrDefaultAsync(a => a.Id == id);
        }
    }
} 