namespace Keel.Application.Sync;

/// <summary>
/// Names of the secrets Keel keeps in the OS secret store (PRD 6.7). Names are not secret; values
/// are. <c>SyncConnection.SecretRef</c> holds <see cref="Connection"/> of its id.
/// </summary>
public static class SecretKeys
{
    /// <summary>Plaid client id (bring your own keys, PRD 14 D2).</summary>
    public const string PlaidClientId = "plaid/client-id";

    /// <summary>Plaid secret for the selected environment.</summary>
    public const string PlaidSecret = "plaid/secret";

    /// <summary>Plaid environment the keys belong to ("sandbox" or "production").</summary>
    public const string PlaidEnvironment = "plaid/environment";

    /// <summary>A SimpleFIN setup token waiting to be claimed (single use).</summary>
    public const string SimpleFinSetupToken = "simplefin/setup-token";

    private const string ConnectionPrefix = "connection/";

    private const string BudgetFilePrefix = "budget-file/";

    /// <summary>The secret of one connection (its access token or access URL, with the environment).</summary>
    public static string Connection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return ConnectionPrefix + (Guid.TryParse(connectionId, out var id) ? id.ToString("N") : connectionId.Trim().ToLowerInvariant());
    }

    /// <summary>The secret of one connection.</summary>
    public static string Connection(Guid connectionId) => ConnectionPrefix + connectionId.ToString("N");

    /// <summary>
    /// The remembered SQLCipher key of one encrypted budget file (F-SET-4); <paramref name="fileId"/> is the
    /// file's salt in hex, readable without the key, so moved copies and backups share it.
    /// </summary>
    public static string BudgetFile(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        return BudgetFilePrefix + fileId.Trim().ToLowerInvariant();
    }
}
