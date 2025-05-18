using Microsoft.EntityFrameworkCore;
using MyApp.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace MyApp.Infrastructure.Data.Repositories
{
    public class Repository<T> : IRepository<T> where T : class
    {
        protected readonly FinanceDbContext _context;
        protected readonly DbSet<T> _dbSet;

        public Repository(FinanceDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbSet = _context.Set<T>();
        }

        public async Task<T?> GetByIdAsync(int id)
        {
            return await _dbSet.FindAsync(id);
        }

        public async Task<IEnumerable<T>> GetAllAsync()
        {
            return await _dbSet.ToListAsync();
        }

        public async Task AddAsync(T entity)
        {
            await _dbSet.AddAsync(entity);
            // SaveChangesAsync will be called by UnitOfWork
        }

        public Task UpdateAsync(T entity)
        {
            _dbSet.Attach(entity); // Attach if not tracked, or use Update which handles this.
            _context.Entry(entity).State = EntityState.Modified;
            // SaveChangesAsync will be called by UnitOfWork
            return Task.CompletedTask;
        }

        public Task DeleteAsync(T entity)
        {
            if (_context.Entry(entity).State == EntityState.Detached)
            {
                _dbSet.Attach(entity);
            }
            _dbSet.Remove(entity);
            // SaveChangesAsync will be called by UnitOfWork
            return Task.CompletedTask;
        }
        
        public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate)
        {
            return await _dbSet.Where(predicate).ToListAsync();
        }
    }
} 