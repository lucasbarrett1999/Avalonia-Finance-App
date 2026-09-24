namespace Keel.Domain.Entities;

/// <summary>A link to an institution through a bank data provider. Secrets live in the OS store.</summary>
public class SyncConnection
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Provider that owns the connection.</summary>
    public SyncProvider Provider { get; set; }

    /// <summary>Institution display name.</summary>
    public string InstitutionName { get; set; } = string.Empty;

    /// <summary>The provider's id for the connection (Plaid item id).</summary>
    public string ExternalItemId { get; set; } = string.Empty;

    /// <summary>Incremental sync cursor; advanced only after a batch commits.</summary>
    public string? Cursor { get; set; }

    /// <summary>Connection health.</summary>
    public SyncStatus Status { get; set; }

    /// <summary>When the last successful sync finished (UTC).</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>Last error message (never contains secrets).</summary>
    public string? LastError { get; set; }

    /// <summary>Key of the secret (access token) in the OS secret store.</summary>
    public string SecretRef { get; set; } = string.Empty;
}
