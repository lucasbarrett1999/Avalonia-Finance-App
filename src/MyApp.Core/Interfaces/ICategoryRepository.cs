using MyApp.Core.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyApp.Core.Interfaces
{
    public interface ICategoryRepository : IRepository<Category>
    {
        Task<Category?> GetCategoryByNameAsync(string name);
        Task<IEnumerable<Category>> GetMainCategoriesAsync(); // Categories without a ParentId
        Task<IEnumerable<Category>> GetSubcategoriesAsync(int parentCategoryId);
    }
} 