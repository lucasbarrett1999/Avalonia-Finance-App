using System.Security.Cryptography;
using System.Text;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Platform;
using Keel.Infrastructure.Platform.Linux;
using Keel.Infrastructure.Platform.MacOS;
using Keel.Infrastructure.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>PRD 6.7 secret stores: the Linux fallback for real, the platform-neutral parts of DPAPI and Keychain, and the selector.</summary>
public sealed class SecretStoreTests : IDisposable
{
    // Cheap Argon2id parameters keep the tests fast; the format stores them, so real files keep their own.
    private static readonly Argon2Parameters Cheap = new(1024, 1, 1);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private EncryptedFileSecretStore FileStore(Func<string>? password = null) =>
        new(Path.Combine(_temp.Path, "secrets"), password ?? (() => "machine-1\nalice"), Cheap);

    [Fact]
    public async Task Fallback_store_round_trips_replaces_and_deletes()
    {
        var store = FileStore();
        (await store.GetAsync("plaid/secret")).ShouldBeNull();

        await store.SetAsync("plaid/secret", "s3cr3t-välue");
        (await store.GetAsync("plaid/secret")).ShouldBe("s3cr3t-välue");
        await store.SetAsync("plaid/secret", "replaced");
        (await FileStore().GetAsync("plaid/secret")).ShouldBe("replaced");

        await store.DeleteAsync("plaid/secret");
        (await store.GetAsync("plaid/secret")).ShouldBeNull();
        await store.DeleteAsync("plaid/secret");
    }

    [Fact]
    public async Task Fallback_files_hold_no_plain_text_and_are_private_to_the_user()
    {
        var store = FileStore();
        await store.SetAsync(SecretKeys.PlaidSecret, "very-secret-token");
        var directory = Path.Combine(_temp.Path, "secrets");
        var file = Path.Combine(directory, SecretFiles.FileName(SecretKeys.PlaidSecret));
        var bytes = await File.ReadAllBytesAsync(file);

        bytes.AsSpan(0, 4).SequenceEqual("KEF1"u8).ShouldBeTrue();
        Encoding.UTF8.GetString(bytes).ShouldNotContain("very-secret-token");
        Directory.GetFiles(directory).Select(Path.GetFileName).ShouldNotContain(n => n!.Contains("plaid", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(file).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Fallback_secrets_do_not_decrypt_for_another_machine_or_user_or_key()
    {
        await FileStore().SetAsync("a", "value-a");
        await FileStore().SetAsync("b", "value-b");

        await Should.ThrowAsync<SecretStoreException>(FileStore(() => "machine-2\nalice").GetAsync("a"));
        await Should.ThrowAsync<SecretStoreException>(FileStore(() => "machine-1\nbob").GetAsync("a"));

        // Moving b's file onto a fails authentication (the key name is associated data).
        var directory = Path.Combine(_temp.Path, "secrets");
        File.Copy(Path.Combine(directory, SecretFiles.FileName("b")), Path.Combine(directory, SecretFiles.FileName("a")), overwrite: true);
        await Should.ThrowAsync<SecretStoreException>(FileStore().GetAsync("a"));
    }

    [Fact]
    public async Task Fallback_detects_tampering()
    {
        await FileStore().SetAsync("k", "value");
        var file = Path.Combine(_temp.Path, "secrets", SecretFiles.FileName("k"));
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(file, bytes);
        await Should.ThrowAsync<SecretStoreException>(FileStore().GetAsync("k"));
    }

    [Fact]
    public void Argon2id_matches_the_RFC_9106_parameters_shape_and_is_deterministic()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var first = EncryptedFileSecretStore.DeriveKey("pw", salt, Cheap);
        var second = EncryptedFileSecretStore.DeriveKey("pw", salt, Cheap);
        first.Length.ShouldBe(32);
        first.ShouldBe(second);
        EncryptedFileSecretStore.DeriveKey("pw2", salt, Cheap).ShouldNotBe(first);
    }

    [Fact]
    public void Machine_secret_uses_the_machine_id_and_user_name()
    {
        var secret = MachineSecret.Read();
        secret.ShouldEndWith("\n" + Environment.UserName);
        if (File.Exists("/etc/machine-id"))
        {
            secret.ShouldStartWith(File.ReadAllText("/etc/machine-id").Trim());
        }
    }

    [Fact]
    public void Secret_file_names_are_hashes_of_the_key()
    {
        var name = SecretFiles.FileName("connection/abc");
        name.ShouldBe(Convert.ToHexStringLower(SHA256.HashData("connection/abc"u8)) + ".secret");
        SecretFiles.FileName("connection/abd").ShouldNotBe(name);
    }

    [Fact]
    public void Dpapi_blobs_are_framed_and_bound_to_their_key()
    {
        var framed = DpapiBlob.Frame([1, 2, 3]);
        framed.ShouldBe("KDP1"u8.ToArray().Concat(new byte[] { 1, 2, 3 }).ToArray());
        DpapiBlob.TryUnframe(framed, out var payload).ShouldBeTrue();
        payload.ShouldBe(new byte[] { 1, 2, 3 });
        DpapiBlob.TryUnframe("KEF1xyz"u8.ToArray(), out _).ShouldBeFalse();
        DpapiBlob.TryUnframe([1, 2], out _).ShouldBeFalse();
        DpapiBlob.Entropy("a").ShouldNotBe(DpapiBlob.Entropy("b"));
    }

    [OsFact("windows")]
    public async Task Dpapi_store_round_trips_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiSecretStore(Path.Combine(_temp.Path, "secrets"));
        await store.SetAsync("k", "dpapi-value");
        (await store.GetAsync("k")).ShouldBe("dpapi-value");
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_temp.Path, "secrets", SecretFiles.FileName("k")))).ShouldNotContain("dpapi-value");
        await store.DeleteAsync("k");
        (await store.GetAsync("k")).ShouldBeNull();
    }

    [Fact]
    public void Keychain_queries_use_generic_passwords_for_the_keel_service()
    {
        var read = KeychainQuery.Read("plaid/secret");
        read.ShouldContain(new KeyValuePair<string, KeychainValue>("kSecClass", new KeychainValue.Constant("kSecClassGenericPassword")));
        read.ShouldContain(new KeyValuePair<string, KeychainValue>("kSecAttrService", new KeychainValue.Text("com.keel.app")));
        read.ShouldContain(new KeyValuePair<string, KeychainValue>("kSecAttrAccount", new KeychainValue.Text("plaid/secret")));
        read.ShouldContain(new KeyValuePair<string, KeychainValue>("kSecReturnData", new KeychainValue.Constant("kCFBooleanTrue")));
        read.ShouldContain(new KeyValuePair<string, KeychainValue>("kSecMatchLimit", new KeychainValue.Constant("kSecMatchLimitOne")));

        var add = KeychainQuery.Add("k", [7]);
        add.Select(p => p.Key).ShouldBe(["kSecClass", "kSecAttrService", "kSecAttrAccount", "kSecAttrLabel", "kSecValueData"]);
        ((KeychainValue.Data)add[^1].Value).Value.ShouldBe(new byte[] { 7 });
        KeychainQuery.Update([9]).ShouldHaveSingleItem().Key.ShouldBe("kSecValueData");
        KeychainQuery.Describe(KeychainQuery.InteractionNotAllowed).ShouldContain("locked");
        KeychainQuery.Describe(-1).ShouldContain("-1");
    }

    [EnvFact("KEEL_TEST_KEYCHAIN")]
    public async Task Keychain_store_round_trips_on_macOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var store = new KeychainSecretStore();
        var key = "keel-test/" + Guid.NewGuid().ToString("N");
        await store.SetAsync(key, "keychain-value");
        await store.SetAsync(key, "keychain-value-2");
        (await store.GetAsync(key)).ShouldBe("keychain-value-2");
        await store.DeleteAsync(key);
        (await store.GetAsync(key)).ShouldBeNull();
    }

    [Fact]
    public void Secret_service_items_are_found_by_service_and_key()
    {
        SecretServiceStore.Attributes("plaid/secret").ShouldBe(new Dictionary<string, string>
        {
            ["xdg:schema"] = "com.keel.app.Secret",
            ["service"] = "com.keel.app",
            ["key"] = "plaid/secret",
        });
    }

    [Fact]
    public async Task Without_a_session_bus_there_is_no_secret_service()
    {
        (await SecretServiceStore.TryConnectAsync(string.Empty, TimeSpan.FromSeconds(1), CancellationToken.None)).ShouldBeNull();
        (await SecretServiceStore.TryConnectAsync("unix:path=/nonexistent/keel-bus", TimeSpan.FromSeconds(1), CancellationToken.None)).ShouldBeNull();
    }

    [OsFact("linux")]
    public async Task On_Linux_the_selector_uses_the_secret_service_or_reports_the_weaker_fallback()
    {
        await using var selector = new SecretStoreSelector(new DataDirectory(_temp.Path), NullLogger<SecretStoreSelector>.Instance);
        var description = await selector.DescribeAsync(CancellationToken.None);

        description.Backend.ShouldBeOneOf(SecretStoreBackend.LinuxSecretService, SecretStoreBackend.EncryptedFile);
        description.IsWeakerFallback.ShouldBe(description.Backend == SecretStoreBackend.EncryptedFile);
        if (description.Backend == SecretStoreBackend.EncryptedFile)
        {
            // Real key derivation (default Argon2id cost) over the real machine id.
            await selector.SetAsync("selector-test", "value");
            (await selector.GetAsync("selector-test")).ShouldBe("value");
            await selector.DeleteAsync("selector-test");
            (await selector.GetAsync("selector-test")).ShouldBeNull();
        }
    }

    /// <summary>
    /// The real libsecret path. Needs a Secret Service on the session bus, e.g.
    /// <c>dbus-run-session -- sh -c 'echo pw | gnome-keyring-daemon --unlock --components=secrets; KEEL_TEST_SECRET_SERVICE=1 dotnet test ...'</c>.
    /// </summary>
    [EnvFact("KEEL_TEST_SECRET_SERVICE", "DBUS_SESSION_BUS_ADDRESS")]
    public async Task Secret_service_store_round_trips_over_dbus()
    {
        var store = await SecretServiceStore.TryConnectAsync(null, TimeSpan.FromSeconds(5), CancellationToken.None);
        store.ShouldNotBeNull();
        await using (store)
        {
            var key = "keel-test/" + Guid.NewGuid().ToString("N");
            (await store.GetAsync(key)).ShouldBeNull();
            await store.SetAsync(key, "dbus-välue");
            (await store.GetAsync(key)).ShouldBe("dbus-välue");
            await store.SetAsync(key, "replaced");
            (await store.GetAsync(key)).ShouldBe("replaced");
            await store.DeleteAsync(key);
            (await store.GetAsync(key)).ShouldBeNull();
        }

        await using var selector = new SecretStoreSelector(new DataDirectory(_temp.Path), NullLogger<SecretStoreSelector>.Instance);
        (await selector.DescribeAsync(CancellationToken.None)).ShouldBe(new SecretStoreDescription(SecretStoreBackend.LinuxSecretService, false));
    }
}
