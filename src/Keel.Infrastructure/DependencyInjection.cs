using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure;

/// <summary>DI registration for infrastructure services.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the data directory, settings.json store, database factory and budget-file
    /// service. Called from the desktop app's composition root only.
    /// </summary>
    public static IServiceCollection AddKeelInfrastructure(this IServiceCollection services, IDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        services.AddSingleton(dataDirectory);
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<KeelDbContextFactory>();
        services.AddSingleton<IDbContextFactory<KeelDbContext>>(sp => sp.GetRequiredService<KeelDbContextFactory>());
        services.AddSingleton<IBudgetFileService, BudgetFileService>();
        return services;
    }
}
