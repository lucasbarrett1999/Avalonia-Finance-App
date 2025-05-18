using Microsoft.EntityFrameworkCore;
using MyApp.Core.Entities;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MyApp.Infrastructure.Data
{
    public static class DbInitializer
    {
        public static async Task InitializeAsync(FinanceDbContext context)
        {
            Console.WriteLine("Attempting to seed database...");

            // Seed Categories
            if (!await context.Categories.AnyAsync())
            {
                Console.WriteLine("Seeding Categories...");
                var categories = new Category[]
                {
                    new Category { Name = "Groceries" },
                    new Category { Name = "Salary" },
                    new Category { Name = "Utilities" },
                    new Category { Name = "Rent/Mortgage" },
                    new Category { Name = "Transportation" },
                    new Category { Name = "Dining Out" },
                    new Category { Name = "Entertainment" },
                    new Category { Name = "Healthcare" },
                    new Category { Name = "Personal Care" },
                    new Category { Name = "Savings" },
                    new Category { Name = "Gifts/Donations" },
                    new Category { Name = "Miscellaneous" }
                };
                await context.Categories.AddRangeAsync(categories);
                await context.SaveChangesAsync();
                Console.WriteLine("Categories seeded.");
            }
            else
            {
                Console.WriteLine("Categories already exist. Skipping seeding.");
            }

            // Seed Accounts
            if (!await context.Accounts.AnyAsync())
            {
                Console.WriteLine("Seeding Accounts...");
                var accounts = new Account[]
                {
                    new Account { Name = "Main Checking", Type = "Checking", Balance = 1000.00m },
                    new Account { Name = "Emergency Fund", Type = "Savings", Balance = 5000.00m },
                    new Account { Name = "Credit Card XYZ", Type = "Credit Card", Balance = -250.00m }
                };
                await context.Accounts.AddRangeAsync(accounts);
                await context.SaveChangesAsync();
                Console.WriteLine("Accounts seeded.");
            }
            else
            {
                Console.WriteLine("Accounts already exist. Skipping seeding.");
            }
            Console.WriteLine("Database seeding attempt completed.");
        }
    }
} 