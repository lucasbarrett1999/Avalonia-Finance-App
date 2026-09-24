namespace Keel.Infrastructure.Platform.MacOS;

/// <summary>A Keychain attribute value: a string, bytes, a boolean or another named constant.</summary>
internal abstract record KeychainValue
{
    /// <summary>A CFString value.</summary>
    public sealed record Text(string Value) : KeychainValue;

    /// <summary>A CFData value.</summary>
    public sealed record Data(byte[] Value) : KeychainValue;

    /// <summary>A Security.framework or CoreFoundation constant, e.g. <c>kSecClassGenericPassword</c>.</summary>
    public sealed record Constant(string Name) : KeychainValue;
}

/// <summary>
/// The platform-neutral shape of the Keychain calls (PRD 6.7): generic-password items with service
/// <see cref="Service"/> and the secret's key as the account. The native layer turns these
/// attribute lists into CFDictionaries; tests check them on any OS.
/// </summary>
internal static class KeychainQuery
{
    /// <summary>The Keychain service name.</summary>
    public const string Service = "com.keel.app";

    /// <summary><c>errSecSuccess</c>.</summary>
    public const int Success = 0;

    /// <summary><c>errSecItemNotFound</c>.</summary>
    public const int ItemNotFound = -25300;

    /// <summary><c>errSecDuplicateItem</c>.</summary>
    public const int DuplicateItem = -25299;

    /// <summary><c>errSecAuthFailed</c>.</summary>
    public const int AuthFailed = -25293;

    /// <summary><c>errSecInteractionNotAllowed</c> (the keychain is locked and no UI may be shown).</summary>
    public const int InteractionNotAllowed = -25308;

    /// <summary><c>errSecUserCanceled</c>.</summary>
    public const int UserCanceled = -128;

    /// <summary>Attributes that identify the item of <paramref name="key"/>.</summary>
    public static IReadOnlyList<KeyValuePair<string, KeychainValue>> Identify(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return
        [
            new("kSecClass", new KeychainValue.Constant("kSecClassGenericPassword")),
            new("kSecAttrService", new KeychainValue.Text(Service)),
            new("kSecAttrAccount", new KeychainValue.Text(key)),
        ];
    }

    /// <summary>The <c>SecItemCopyMatching</c> query that returns the item's data.</summary>
    public static IReadOnlyList<KeyValuePair<string, KeychainValue>> Read(string key) =>
    [
        .. Identify(key),
        new("kSecReturnData", new KeychainValue.Constant("kCFBooleanTrue")),
        new("kSecMatchLimit", new KeychainValue.Constant("kSecMatchLimitOne")),
    ];

    /// <summary>The <c>SecItemAdd</c> attributes of a new item.</summary>
    public static IReadOnlyList<KeyValuePair<string, KeychainValue>> Add(string key, byte[] value) =>
    [
        .. Identify(key),
        new("kSecAttrLabel", new KeychainValue.Text("Keel: " + key)),
        new("kSecValueData", new KeychainValue.Data(value)),
    ];

    /// <summary>The <c>SecItemUpdate</c> attributes that replace the item's data.</summary>
    public static IReadOnlyList<KeyValuePair<string, KeychainValue>> Update(byte[] value) =>
        [new("kSecValueData", new KeychainValue.Data(value))];

    /// <summary>A message for a failed status; never contains the secret or the key.</summary>
    public static string Describe(int status) => status switch
    {
        AuthFailed => "The Keychain refused access (authorization failed).",
        InteractionNotAllowed => "The Keychain is locked.",
        UserCanceled => "Keychain access was cancelled.",
        DuplicateItem => "The Keychain already has this item.",
        _ => $"The Keychain returned status {status}.",
    };
}
