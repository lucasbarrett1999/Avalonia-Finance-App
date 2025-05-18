using MyApp.Core.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyApp.Core.Interfaces
{
    public interface ITransactionRepository : IRepository<Transaction>
    {
        Task<IEnumerable<Transaction>> GetTransactionsByAccountIdAsync(int accountId);
        Task<IEnumerable<Transaction>> GetTransactionsByDateRangeAsync(DateTime startDate, DateTime endDate);
        // Potentially include related entities like Account and Category
        Task<Transaction?> GetTransactionWithDetailsAsync(int transactionId); 
    }
} 