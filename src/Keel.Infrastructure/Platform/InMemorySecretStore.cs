using System.Collections.Concurrent;
using Keel.Application.Security;

namespace Keel.Infrastructure.Platform;

/// <summary>A secret store in process memory, for tests. Nothing is persisted.</summary>
public sealed class InMemorySecretStore : ISecretStore, ISecretStoreInfo
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Number of stored secrets.</summary>
    public int Count => _values.Count;

    /// <summary>Whether <paramref name="key"/> is stored.</summary>
    public bool Contains(string key) => _values.ContainsKey(key);

    /// <inheritdoc />
    public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    /// <inheritdoc />
    public Task SetAsync(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SecretStoreDescription> DescribeAsync(CancellationToken ct) =>
        Task.FromResult(new SecretStoreDescription(SecretStoreBackend.InMemory, IsWeakerFallback: false));
}
