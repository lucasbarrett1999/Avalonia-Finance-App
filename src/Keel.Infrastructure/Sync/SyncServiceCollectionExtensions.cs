using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Infrastructure.Platform;
using Keel.Infrastructure.Sync.Plaid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keel.Infrastructure.Sync;

/// <summary>DI registration for bank sync (M7).</summary>
public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OS secret store (<see cref="SecretStoreSelector"/>; a store registered earlier
    /// wins, for tests), the Plaid provider over a resilient <see cref="HttpClient"/>, credentials,
    /// and <see cref="ISyncService"/>.
    /// </summary>
    public static IServiceCollection AddKeelSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<SecretStoreSelector>();
        services.TryAddSingleton<ISecretStore>(sp => sp.GetRequiredService<SecretStoreSelector>());
        services.TryAddSingleton<ISecretStoreInfo>(sp => sp.GetRequiredService<SecretStoreSelector>());

        services.AddHttpClient(GoingPlaidApi.HttpClientName)
            .AddStandardResilienceHandler(options =>
            {
                // transactions/sync pages can take a while; retries cover 5xx, 408, 429 and transport errors.
                options.Retry.MaxRetryAttempts = 3;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(4);
            });
        services.TryAddSingleton<IPlaidApi, GoingPlaidApi>();
        services.TryAddSingleton(new PlaidProviderOptions());
        services.AddSingleton<IBankDataProvider, PlaidProvider>();

        services.AddSingleton<IBankCredentialsService, BankCredentialsService>();
        services.AddSingleton<ISyncService, SyncService>();
        return services;
    }
}
