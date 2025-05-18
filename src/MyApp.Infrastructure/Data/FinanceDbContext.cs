using Microsoft.EntityFrameworkCore;
using MyApp.Core.Entities;

namespace MyApp.Infrastructure.Data
{
    public class FinanceDbContext : DbContext
    {
        public DbSet<Account> Accounts { get; set; } = null!;
        public DbSet<Transaction> Transactions { get; set; } = null!;
        public DbSet<Category> Categories { get; set; } = null!;
        public DbSet<PlaidItem> PlaidItems { get; set; } = null!;

        public FinanceDbContext(DbContextOptions<FinanceDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Configure the database (SQLite in this case) to be used if not already configured.
            // Connection string will typically be passed in via options from DI,
            // but can be specified here as a fallback or for simpler scenarios.
            if (!optionsBuilder.IsConfigured)
            {
                // This is a placeholder and should ideally be configured via DI
                // optionsBuilder.UseSqlite("Data Source=myapp.db");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure relationships using Fluent API if defaults are not sufficient
            // Example: One-to-many for Account and Transactions (EF Core infers this by convention)
            modelBuilder.Entity<Account>()
                .HasMany(a => a.Transactions)
                .WithOne(t => t.Account)
                .HasForeignKey(t => t.AccountId);

            // Example: One-to-many for Category and Transactions
            modelBuilder.Entity<Category>()
                .HasMany(c => c.Transactions)
                .WithOne(t => t.Category)
                .HasForeignKey(t => t.CategoryId)
                .IsRequired(false); // CategoryId is nullable in Transaction

            // Example: Self-referencing for Category (Parent/Subcategories)
            modelBuilder.Entity<Category>()
                .HasMany(c => c.Subcategories)
                .WithOne(c => c.ParentCategory)
                .HasForeignKey(c => c.ParentCategoryId)
                .IsRequired(false); // ParentCategoryId is nullable
                
            // PlaidItem configuration
            modelBuilder.Entity<PlaidItem>()
                .HasIndex(p => p.ItemId)
                .IsUnique();
                
            // Relationship between PlaidItem and Account - using item_id (string column) as the link
            modelBuilder.Entity<PlaidItem>()
                .HasMany(p => p.Accounts)
                .WithOne()
                .HasPrincipalKey(p => p.ItemId) // Use ItemId as the principal key
                .HasForeignKey(a => a.PlaidItemId)
                .IsRequired(false); // PlaidItemId is nullable in Account
        }
    }
} 