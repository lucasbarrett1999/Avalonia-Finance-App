using System;
using System.Threading.Tasks;

namespace MyApp.Core.Interfaces
{
    public interface IUnitOfWork : IDisposable
    {
        IAccountRepository AccountRepository { get; }
        ITransactionRepository TransactionRepository { get; }
        ICategoryRepository CategoryRepository { get; }
        IPlaidItemRepository PlaidItemRepository { get; }

        Task<int> CompleteAsync();
    }
} 