using Microsoft.EntityFrameworkCore;
using MyApp.Core.Entities;
using MyApp.Core.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyApp.Infrastructure.Data.Repositories
{
    public class CategoryRepository : Repository<Category>, ICategoryRepository
    {
        public CategoryRepository(FinanceDbContext context) : base(context)
        {
        }

        public async Task<Category?> GetCategoryByNameAsync(string name)
        {
            return await _dbSet.FirstOrDefaultAsync(c => c.Name == name);
        }

        public async Task<IEnumerable<Category>> GetMainCategoriesAsync()
        {
            return await _dbSet
                .Where(c => c.ParentCategoryId == null)
                .Include(c => c.Subcategories) // Eager load subcategories
                .ToListAsync();
        }

        public async Task<IEnumerable<Category>> GetSubcategoriesAsync(int parentCategoryId)
        {
            return await _dbSet
                .Where(c => c.ParentCategoryId == parentCategoryId)
                .ToListAsync();
        }
    }
} 