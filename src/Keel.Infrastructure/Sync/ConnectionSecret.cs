using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Domain;

namespace Keel.Infrastructure.Sync;

/// <summary>
/// What the OS secret store holds for one connection (key <see cref="SecretKeys.Connection(string)"/>):
/// the Plaid access token with the environment it belongs to, or the SimpleFIN access URL.
/// Serialized as JSON; never logged, never in the database.
/// </summary>
internal sealed record ConnectionSecret
{
    /// <summary>Format version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>"plaid" or "simplefin".</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    /// <summary>Plaid environment of the token ("sandbox", "production").</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>Plaid access token.</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; init; }

    /// <summary>Plaid item id.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }

    /// <summary>SimpleFIN access URL (with its embedded credentials).</summary>
    [JsonPropertyName("accessUrl")]
    public string? AccessUrl { get; init; }

    /// <summary>Loads the secret of a connection; throws <see cref="BankProviderException"/> when it is missing or unreadable.</summary>
    public static async Task<ConnectionSecret> LoadAsync(ISecretStore store, string connectionId)
    {
        var json = await store.GetAsync(SecretKeys.Connection(connectionId)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            throw new BankProviderException(SyncStatus.NeedsReauth, BankErrorCodes.MissingAccessToken, "The connection's access token is not in this machine's secret store.");
        }

        try
        {
            return JsonSerializer.Deserialize<ConnectionSecret>(json) ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.MissingAccessToken, "The connection's stored secret is unreadable.", ex);
        }
    }

    /// <summary>Stores the secret of a connection.</summary>
    public Task SaveAsync(ISecretStore store, string connectionId) =>
        store.SetAsync(SecretKeys.Connection(connectionId), JsonSerializer.Serialize(this));
}
