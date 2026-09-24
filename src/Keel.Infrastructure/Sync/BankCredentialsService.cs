using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Infrastructure.Sync.Plaid;

namespace Keel.Infrastructure.Sync;

/// <summary>
/// Provider credentials in the OS secret store (F-SET-3, PRD 6.7). The UI can only learn whether a
/// value is present; values are never returned, rendered or logged.
/// </summary>
public sealed class BankCredentialsService(ISecretStore secrets, ISecretStoreInfo storeInfo) : IBankCredentialsService
{
    /// <inheritdoc />
    public async Task<PlaidCredentialsStatus> GetPlaidStatusAsync(CancellationToken ct)
    {
        var clientId = await secrets.GetAsync(SecretKeys.PlaidClientId).WaitAsync(ct).ConfigureAwait(false);
        var secret = await secrets.GetAsync(SecretKeys.PlaidSecret).WaitAsync(ct).ConfigureAwait(false);
        var environment = PlaidCredentials.ParseEnvironment(await secrets.GetAsync(SecretKeys.PlaidEnvironment).WaitAsync(ct).ConfigureAwait(false));
        return new PlaidCredentialsStatus(!string.IsNullOrWhiteSpace(clientId), !string.IsNullOrWhiteSpace(secret), environment ?? PlaidEnvironment.Sandbox);
    }

    /// <inheritdoc />
    public Task SetPlaidClientIdAsync(string? clientId, CancellationToken ct) => SetOrDeleteAsync(SecretKeys.PlaidClientId, clientId, ct);

    /// <inheritdoc />
    public Task SetPlaidSecretAsync(string? secret, CancellationToken ct) => SetOrDeleteAsync(SecretKeys.PlaidSecret, secret, ct);

    /// <inheritdoc />
    public Task SetPlaidEnvironmentAsync(PlaidEnvironment environment, CancellationToken ct) =>
        secrets.SetAsync(SecretKeys.PlaidEnvironment, PlaidCredentials.EnvironmentName(environment)).WaitAsync(ct);

    /// <inheritdoc />
    public async Task<bool> HasSimpleFinSetupTokenAsync(CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(await secrets.GetAsync(SecretKeys.SimpleFinSetupToken).WaitAsync(ct).ConfigureAwait(false));

    /// <inheritdoc />
    public Task SetSimpleFinSetupTokenAsync(string? setupToken, CancellationToken ct) => SetOrDeleteAsync(SecretKeys.SimpleFinSetupToken, setupToken, ct);

    /// <inheritdoc />
    public Task<SecretStoreDescription> DescribeStoreAsync(CancellationToken ct) => storeInfo.DescribeAsync(ct);

    private Task SetOrDeleteAsync(string key, string? value, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(value)
            ? secrets.DeleteAsync(key).WaitAsync(ct)
            : secrets.SetAsync(key, value.Trim()).WaitAsync(ct);
}
