using MyApp.Core.Interfaces;
using MyApp.Infrastructure.Data.Repositories; // Needed for concrete repository types
using System.Threading.Tasks;

namespace MyApp.Infrastructure.Data
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly FinanceDbContext _context;
        public IAccountRepository AccountRepository { get; private set; }
        public ITransactionRepository TransactionRepository { get; private set; }
        public ICategoryRepository CategoryRepository { get; private set; }
        public IPlaidItemRepository PlaidItemRepository { get; private set; }

        public UnitOfWork(FinanceDbContext context)
        {
            _context = context;
            AccountRepository = new AccountRepository(_context);
            TransactionRepository = new TransactionRepository(_context);
            CategoryRepository = new CategoryRepository(_context);
            PlaidItemRepository = new PlaidItemRepository(_context);
        }

        public async Task<int> CompleteAsync()
        {
            return await _context.SaveChangesAsync();
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
} 