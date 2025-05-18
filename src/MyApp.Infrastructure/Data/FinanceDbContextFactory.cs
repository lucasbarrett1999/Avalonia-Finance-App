using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace MyApp.Infrastructure.Data
{
    public class FinanceDbContextFactory : IDesignTimeDbContextFactory<FinanceDbContext>
    {
        public FinanceDbContext CreateDbContext(string[] args)
        {
            // The startup project is MyApp.csproj. When EF tools run, they set the
            // current directory to the startup project's directory.
            // So, Directory.GetCurrentDirectory() should resolve to the MyApp/ folder.
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory()) 
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) // Ensure it's found
                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<FinanceDbContext>();
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                // Provide a more specific error if appsettings.json itself wasn't found or connection string is missing
                var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
                if (!File.Exists(appSettingsPath))
                {
                    throw new FileNotFoundException($"appsettings.json not found at expected startup project path: {appSettingsPath}. Ensure the EF Core tools are run with the correct startup project ('MyApp').");
                }
                throw new InvalidOperationException($"Connection string 'DefaultConnection' not found in {appSettingsPath}.");
            }

            optionsBuilder.UseSqlite(connectionString);

            return new FinanceDbContext(optionsBuilder.Options);
        }
    }
} 