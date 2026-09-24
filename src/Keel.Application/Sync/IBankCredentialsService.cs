using Keel.Application.Security;

namespace Keel.Application.Sync;

/// <summary>
/// Provider credentials entered in Settings → Connections (F-SET-3), kept only in the OS secret
/// store (PRD 6.7). Values are write-only for the UI: it can learn whether a value is set, never read it.
/// </summary>
public interface IBankCredentialsService
{
    /// <summary>Whether Plaid keys are set and which environment they belong to.</summary>
    Task<PlaidCredentialsStatus> GetPlaidStatusAsync(CancellationToken ct);

    /// <summary>Stores (replaces) the Plaid client id; blank removes it.</summary>
    Task SetPlaidClientIdAsync(string? clientId, CancellationToken ct);

    /// <summary>Stores (replaces) the Plaid secret; blank removes it.</summary>
    Task SetPlaidSecretAsync(string? secret, CancellationToken ct);

    /// <summary>Stores the environment new links use.</summary>
    Task SetPlaidEnvironmentAsync(PlaidEnvironment environment, CancellationToken ct);

    /// <summary>Whether a SimpleFIN setup token is waiting to be claimed.</summary>
    Task<bool> HasSimpleFinSetupTokenAsync(CancellationToken ct);

    /// <summary>Stores (replaces) a SimpleFIN setup token; blank removes it.</summary>
    Task SetSimpleFinSetupTokenAsync(string? setupToken, CancellationToken ct);

    /// <summary>The secret store in use (backend name and the Linux fallback warning).</summary>
    Task<SecretStoreDescription> DescribeStoreAsync(CancellationToken ct);
}

/// <summary>Plaid API environment (PRD 7.5).</summary>
public enum PlaidEnvironment
{
    /// <summary>Test data; sandbox credentials <c>user_good</c> / <c>pass_good</c>.</summary>
    Sandbox,

    /// <summary>Real institutions.</summary>
    Production,
}

/// <summary>Which Plaid values are stored (never the values themselves).</summary>
/// <param name="HasClientId">Client id stored.</param>
/// <param name="HasSecret">Secret stored.</param>
/// <param name="Environment">Environment for new links.</param>
public sealed record PlaidCredentialsStatus(bool HasClientId, bool HasSecret, PlaidEnvironment Environment)
{
    /// <summary>Whether both keys are present.</summary>
    public bool IsComplete => HasClientId && HasSecret;
}
