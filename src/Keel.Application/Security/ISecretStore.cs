namespace Keel.Application.Security;

/// <summary>
/// The OS secret store (PRD 6.7): DPAPI on Windows, Keychain on macOS, libsecret (with an
/// encrypted-file fallback) on Linux. Secrets are never stored in the database or logged.
/// Implemented in M7.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret for <paramref name="key"/>, or null when absent.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Creates or replaces a secret.</summary>
    Task SetAsync(string key, string value);

    /// <summary>Deletes a secret; no-op when absent.</summary>
    Task DeleteAsync(string key);
}
