using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Infrastructure.Platform.Linux;
using Keel.Infrastructure.Platform.MacOS;
using Keel.Infrastructure.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Platform;

/// <summary>
/// The app's <see cref="ISecretStore"/> (PRD 6.7): picks the OS store once, on first use or when
/// <see cref="DescribeAsync"/> is called at startup, and forwards every call to it. Windows uses
/// DPAPI, macOS the Keychain, Linux the Secret Service over D-Bus when one answers and otherwise the
/// encrypted-file fallback (reported as weaker). Values and key names are never logged.
/// </summary>
public sealed partial class SecretStoreSelector : ISecretStore, ISecretStoreInfo, IAsyncDisposable
{
    /// <summary>How long to wait for a Secret Service on the session bus.</summary>
    public static readonly TimeSpan SecretServiceTimeout = TimeSpan.FromSeconds(3);

    private readonly IDataDirectory _dataDirectory;
    private readonly ILogger<SecretStoreSelector> _logger;
    private readonly Lazy<Task<Selected>> _selected;

    /// <summary>Creates the selector; nothing is probed until first use.</summary>
    public SecretStoreSelector(IDataDirectory dataDirectory, ILogger<SecretStoreSelector> logger)
    {
        _dataDirectory = dataDirectory;
        _logger = logger;
        _selected = new(() => Task.Run(SelectAsync), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public async Task<SecretStoreDescription> DescribeAsync(CancellationToken ct) =>
        (await _selected.Value.WaitAsync(ct).ConfigureAwait(false)).Description;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key) => await (await StoreAsync().ConfigureAwait(false)).GetAsync(key).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetAsync(string key, string value) => await (await StoreAsync().ConfigureAwait(false)).SetAsync(key, value).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task DeleteAsync(string key) => await (await StoreAsync().ConfigureAwait(false)).DeleteAsync(key).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_selected.IsValueCreated && _selected.Value.IsCompletedSuccessfully && _selected.Value.Result.Store is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ISecretStore> StoreAsync() => (await _selected.Value.ConfigureAwait(false)).Store;

    private async Task<Selected> SelectAsync()
    {
        Selected selected;
        if (OperatingSystem.IsWindows())
        {
            selected = new(new DpapiSecretStore(_dataDirectory.SecretsDirectory), new(SecretStoreBackend.WindowsDpapi, false));
        }
        else if (OperatingSystem.IsMacOS())
        {
            selected = new(new KeychainSecretStore(), new(SecretStoreBackend.MacOSKeychain, false));
        }
        else if (await SecretServiceStore.TryConnectAsync(null, SecretServiceTimeout, CancellationToken.None).ConfigureAwait(false) is { } secretService)
        {
            selected = new(secretService, new(SecretStoreBackend.LinuxSecretService, false));
        }
        else
        {
            selected = new(new EncryptedFileSecretStore(_dataDirectory.SecretsDirectory), new(SecretStoreBackend.EncryptedFile, true));
        }

        LogSelected(_logger, selected.Description.Backend, selected.Description.IsWeakerFallback);
        return selected;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Secret store: {Backend} (weaker fallback: {IsFallback})")]
    private static partial void LogSelected(ILogger logger, SecretStoreBackend backend, bool isFallback);

    private sealed record Selected(ISecretStore Store, SecretStoreDescription Description);
}
