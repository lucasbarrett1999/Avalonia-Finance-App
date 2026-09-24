using System.Security.Cryptography;
using System.Text;
using Keel.Application.Security;
using Tmds.DBus.Protocol;

namespace Keel.Infrastructure.Platform.Linux;

/// <summary>
/// Linux secret store (PRD 6.7): the freedesktop Secret Service (<c>org.freedesktop.secrets</c>,
/// served by GNOME Keyring, KWallet or KeePassXC) over the session D-Bus, the same store libsecret
/// uses. Items live in the default collection with the attributes <c>service=com.keel.app</c> and
/// <c>key=&lt;secret key&gt;</c>. A locked collection or item is unlocked through the service's
/// own prompt. The session uses the "plain" algorithm: the bus is local to the user's login.
/// </summary>
internal sealed class SecretServiceStore : ISecretStore, IAsyncDisposable
{
    /// <summary>Attribute value of <c>service</c> on every Keel item.</summary>
    public const string ServiceAttribute = "com.keel.app";

    /// <summary>libsecret-compatible schema name.</summary>
    public const string SchemaName = "com.keel.app.Secret";

    private const string BusName = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string NoPrompt = "/";

    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);

    private readonly Connection _connection;
    private readonly string _session;
    private readonly string _collection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SecretServiceStore(Connection connection, string session, string collection)
    {
        _connection = connection;
        _session = session;
        _collection = collection;
    }

    /// <summary>
    /// Connects to the Secret Service on the bus at <paramref name="address"/> (the session bus when
    /// null) and opens a session on the default collection; null when there is no bus, no service, or
    /// no default collection, or the service does not answer within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<SecretServiceStore?> TryConnectAsync(string? address, TimeSpan timeout, CancellationToken ct)
    {
        address ??= Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        var connection = new Connection(address);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await connection.ConnectAsync().AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
            var services = await connection.ListServicesAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            var activatable = await connection.ListActivatableServicesAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            if (!services.Contains(BusName) && !activatable.Contains(BusName))
            {
                connection.Dispose();
                return null;
            }

            var session = await OpenSessionAsync(connection).WaitAsync(cts.Token).ConfigureAwait(false);
            var collection = await ReadAliasAsync(connection, "default").WaitAsync(cts.Token).ConfigureAwait(false);
            if (collection == NoPrompt)
            {
                connection.Dispose();
                return null;
            }

            return new SecretServiceStore(connection, session, collection);
        }
        catch (Exception ex) when (ex is DBusException or ConnectException or IOException or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            connection.Dispose();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    /// <summary>The attributes that identify the item of <paramref name="key"/>.</summary>
    public static IReadOnlyDictionary<string, string> Attributes(string key) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["xdg:schema"] = SchemaName,
        ["service"] = ServiceAttribute,
        ["key"] = key,
    };

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var items = await FindUnlockedAsync(key).ConfigureAwait(false);
            if (items.Count == 0)
            {
                return null;
            }

            var bytes = await GetSecretAsync(items[0]).ConfigureAwait(false);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (DBusException ex)
        {
            throw new SecretStoreException("The Secret Service could not read a secret (" + ex.ErrorName + ").", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync().ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            await EnsureUnlockedAsync([_collection]).ConfigureAwait(false);
            var (_, prompt) = await _connection.CallMethodAsync(
                CreateItemMessage(key, bytes),
                static (m, _) =>
                {
                    var reader = m.GetBodyReader();
                    return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
                },
                null).ConfigureAwait(false);
            if (prompt != NoPrompt && await PromptAsync(prompt).ConfigureAwait(false))
            {
                throw new SecretStoreException("Saving the secret was cancelled at the keyring prompt.");
            }
        }
        catch (DBusException ex)
        {
            throw new SecretStoreException("The Secret Service could not save a secret (" + ex.ErrorName + ").", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var item in await FindUnlockedAsync(key).ConfigureAwait(false))
            {
                var prompt = await _connection.CallMethodAsync(
                    Call(item, ItemInterface, "Delete", null),
                    static (m, _) => m.GetBodyReader().ReadObjectPathAsString(),
                    null).ConfigureAwait(false);
                if (prompt != NoPrompt)
                {
                    await PromptAsync(prompt).ConfigureAwait(false);
                }
            }
        }
        catch (DBusException ex)
        {
            throw new SecretStoreException("The Secret Service could not delete a secret (" + ex.ErrorName + ").", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<string> OpenSessionAsync(Connection connection)
    {
        MessageBuffer Message()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BusName, ServicePath, ServiceInterface, "OpenSession", "sv", MessageFlags.None);
            writer.WriteString("plain");
            writer.WriteVariantString(string.Empty);
            return writer.CreateMessage();
        }

        return await connection.CallMethodAsync(
            Message(),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadVariantValue();
                return reader.ReadObjectPathAsString();
            },
            null).ConfigureAwait(false);
    }

    private static async Task<string> ReadAliasAsync(Connection connection, string alias)
    {
        MessageBuffer Message()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BusName, ServicePath, ServiceInterface, "ReadAlias", "s", MessageFlags.None);
            writer.WriteString(alias);
            return writer.CreateMessage();
        }

        return await connection.CallMethodAsync(Message(), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
    }

    // Items of the key, unlocking locked ones through the service prompt.
    private async Task<IReadOnlyList<string>> FindUnlockedAsync(string key)
    {
        MessageBuffer Message()
        {
            var writer = _connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(BusName, ServicePath, ServiceInterface, "SearchItems", "a{ss}", MessageFlags.None);
                WriteAttributes(ref writer, Attributes(key));
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        var (unlocked, locked) = await _connection.CallMethodAsync(
            Message(),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var u = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                var l = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToArray();
                return (u, l);
            },
            null).ConfigureAwait(false);
        if (locked.Length == 0)
        {
            return unlocked;
        }

        await EnsureUnlockedAsync(locked).ConfigureAwait(false);
        return [.. unlocked, .. locked];
    }

    private async Task EnsureUnlockedAsync(IReadOnlyList<string> objects)
    {
        if (objects.Count == 1 && objects[0] == _collection && !await IsLockedAsync(_collection).ConfigureAwait(false))
        {
            return;
        }

        MessageBuffer Message()
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BusName, ServicePath, ServiceInterface, "Unlock", "ao", MessageFlags.None);
            writer.WriteArray(objects.Select(o => new ObjectPath(o)).ToArray());
            return writer.CreateMessage();
        }

        var prompt = await _connection.CallMethodAsync(
            Message(),
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadArrayOfObjectPath();
                return reader.ReadObjectPathAsString();
            },
            null).ConfigureAwait(false);
        if (prompt != NoPrompt && await PromptAsync(prompt).ConfigureAwait(false))
        {
            throw new SecretStoreException("The keyring was not unlocked.");
        }
    }

    private async Task<bool> IsLockedAsync(string path)
    {
        MessageBuffer Message()
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BusName, path, PropertiesInterface, "Get", "ss", MessageFlags.None);
            writer.WriteString(CollectionInterface);
            writer.WriteString("Locked");
            return writer.CreateMessage();
        }

        return await _connection.CallMethodAsync(Message(), static (m, _) => m.GetBodyReader().ReadVariantValue().GetBool(), null).ConfigureAwait(false);
    }

    private async Task<byte[]> GetSecretAsync(string item)
    {
        return await _connection.CallMethodAsync(
            Call(item, ItemInterface, "GetSecret", _session),
            static (m, _) =>
            {
                // (oayays): session, parameters, value, content type.
                var reader = m.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.ReadArrayOfByte();
                var value = reader.ReadArrayOfByte();
                reader.ReadString();
                return value;
            },
            null).ConfigureAwait(false);
    }

    // Runs a prompt and waits for its Completed signal; returns true when the user dismissed it.
    private async Task<bool> PromptAsync(string prompt)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = PromptInterface, Member = "Completed", Path = prompt },
            static (m, _) => m.GetBodyReader().ReadBool(),
            (ex, dismissed, _, _) =>
            {
                if (ex is not null)
                {
                    completed.TrySetException(ex);
                }
                else
                {
                    completed.TrySetResult(dismissed);
                }
            },
            null,
            null,
            emitOnCapturedContext: false,
            ObserverFlags.None).ConfigureAwait(false);

        MessageBuffer Message()
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BusName, prompt, PromptInterface, "Prompt", "s", MessageFlags.None);
            writer.WriteString(string.Empty);
            return writer.CreateMessage();
        }

        await _connection.CallMethodAsync(Message()).ConfigureAwait(false);
        try
        {
            return await completed.Task.WaitAsync(PromptTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new SecretStoreException("The keyring prompt was not answered.", ex);
        }
    }

    private MessageBuffer CreateItemMessage(string key, byte[] secret)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            return CreateItemBody(ref writer, key, secret);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private MessageBuffer CreateItemBody(ref MessageWriter writer, string key, byte[] secret)
    {
        writer.WriteMethodCallHeader(BusName, _collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b", MessageFlags.None);

        var properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString("Keel: " + key);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        writer.WriteSignature("a{ss}");
        WriteAttributes(ref writer, Attributes(key));
        writer.WriteDictionaryEnd(properties);

        writer.WriteStructureStart();
        writer.WriteObjectPath(_session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(secret);
        writer.WriteString("text/plain; charset=utf8");

        writer.WriteBool(true);
        return writer.CreateMessage();
    }

    private MessageBuffer Call(string path, string @interface, string member, string? objectPathArgument)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BusName, path, @interface, member, objectPathArgument is null ? null : "o", MessageFlags.None);
        if (objectPathArgument is not null)
        {
            writer.WriteObjectPath(objectPathArgument);
        }

        return writer.CreateMessage();
    }

    private static void WriteAttributes(ref MessageWriter writer, IReadOnlyDictionary<string, string> attributes)
    {
        var start = writer.WriteDictionaryStart();
        foreach (var (name, value) in attributes)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(name);
            writer.WriteString(value);
        }

        writer.WriteDictionaryEnd(start);
    }
}
