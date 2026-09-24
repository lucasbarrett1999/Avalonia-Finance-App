using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Keel.Application.Security;

namespace Keel.Infrastructure.Platform.MacOS;

/// <summary>
/// macOS secret store (PRD 6.7): generic passwords in the user's default Keychain through
/// Security.framework (<c>SecItemAdd</c>, <c>SecItemCopyMatching</c>, <c>SecItemUpdate</c>,
/// <c>SecItemDelete</c>), service <c>com.keel.app</c>, account = secret key.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class KeychainSecretStore : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public Task<string?> GetAsync(string key) => RunAsync(() =>
    {
        var status = KeychainNative.CopyData(KeychainQuery.Read(key), out var data);
        if (status == KeychainQuery.ItemNotFound)
        {
            return null;
        }

        ThrowIfFailed(status);
        try
        {
            return Encoding.UTF8.GetString(data!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    });

    /// <inheritdoc />
    public Task SetAsync(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return RunAsync<object?>(() =>
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                var status = KeychainNative.Update(KeychainQuery.Identify(key), KeychainQuery.Update(bytes));
                if (status == KeychainQuery.ItemNotFound)
                {
                    status = KeychainNative.Add(KeychainQuery.Add(key, bytes));
                }

                ThrowIfFailed(status);
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        });
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key) => RunAsync<object?>(() =>
    {
        var status = KeychainNative.Delete(KeychainQuery.Identify(key));
        if (status != KeychainQuery.ItemNotFound)
        {
            ThrowIfFailed(status);
        }

        return null;
    });

    private static void ThrowIfFailed(int status)
    {
        if (status != KeychainQuery.Success)
        {
            throw new SecretStoreException(KeychainQuery.Describe(status));
        }
    }

    // Keychain calls may block on a system prompt; keep them off the caller's thread and serialized.
    private async Task<T> RunAsync<T>(Func<T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>P/Invoke to Security.framework and CoreFoundation (macOS only).</summary>
[SupportedOSPlatform("macos")]
internal static partial class KeychainNative
{
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly Lazy<IntPtr> Security = new(() => NativeLibrary.Load(SecurityLibrary));
    private static readonly Lazy<IntPtr> CoreFoundation = new(() => NativeLibrary.Load(CoreFoundationLibrary));

    /// <summary><c>SecItemCopyMatching</c> returning the item's data.</summary>
    public static int CopyData(IReadOnlyList<KeyValuePair<string, KeychainValue>> query, out byte[]? data)
    {
        data = null;
        using var dictionary = new CFDictionary(query);
        var status = SecItemCopyMatching(dictionary.Handle, out var result);
        if (status != KeychainQuery.Success)
        {
            return status;
        }

        try
        {
            var length = checked((int)CFDataGetLength(result));
            data = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(result), data, 0, length);
            return status;
        }
        finally
        {
            CFRelease(result);
        }
    }

    /// <summary><c>SecItemAdd</c>.</summary>
    public static int Add(IReadOnlyList<KeyValuePair<string, KeychainValue>> attributes)
    {
        using var dictionary = new CFDictionary(attributes);
        return SecItemAdd(dictionary.Handle, IntPtr.Zero);
    }

    /// <summary><c>SecItemUpdate</c>.</summary>
    public static int Update(IReadOnlyList<KeyValuePair<string, KeychainValue>> query, IReadOnlyList<KeyValuePair<string, KeychainValue>> attributes)
    {
        using var q = new CFDictionary(query);
        using var a = new CFDictionary(attributes);
        return SecItemUpdate(q.Handle, a.Handle);
    }

    /// <summary><c>SecItemDelete</c>.</summary>
    public static int Delete(IReadOnlyList<KeyValuePair<string, KeychainValue>> query)
    {
        using var dictionary = new CFDictionary(query);
        return SecItemDelete(dictionary.Handle);
    }

    // The value of an exported CFTypeRef constant (e.g. kSecClass) from Security or CoreFoundation.
    private static IntPtr Constant(string name)
    {
        var library = name.StartsWith("kCF", StringComparison.Ordinal) ? CoreFoundation.Value : Security.Value;
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    // The address of an exported struct (the dictionary callbacks).
    private static IntPtr Export(string name) => NativeLibrary.GetExport(CoreFoundation.Value, name);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityLibrary)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundationLibrary, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, string chars, nint length);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial void CFRelease(IntPtr handle);

    /// <summary>A CFDictionary built from attribute pairs; releases what it created.</summary>
    private sealed class CFDictionary : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public CFDictionary(IReadOnlyList<KeyValuePair<string, KeychainValue>> attributes)
        {
            var keys = new IntPtr[attributes.Count];
            var values = new IntPtr[attributes.Count];
            for (var i = 0; i < attributes.Count; i++)
            {
                keys[i] = Constant(attributes[i].Key);
                values[i] = attributes[i].Value switch
                {
                    KeychainValue.Text text => Own(CFStringCreateWithCharacters(IntPtr.Zero, text.Value, text.Value.Length)),
                    KeychainValue.Data data => Own(CFDataCreate(IntPtr.Zero, data.Value, data.Value.Length)),
                    KeychainValue.Constant constant => Constant(constant.Name),
                    _ => throw new ArgumentOutOfRangeException(nameof(attributes)),
                };
            }

            Handle = CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, Export("kCFTypeDictionaryKeyCallBacks"), Export("kCFTypeDictionaryValueCallBacks"));
            if (Handle == IntPtr.Zero)
            {
                Dispose();
                throw new SecretStoreException("Could not build a Keychain query.");
            }
        }

        public IntPtr Handle { get; }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CFRelease(Handle);
            }

            foreach (var owned in _owned)
            {
                CFRelease(owned);
            }

            _owned.Clear();
        }

        private IntPtr Own(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                throw new SecretStoreException("Could not build a Keychain value.");
            }

            _owned.Add(handle);
            return handle;
        }
    }
}
