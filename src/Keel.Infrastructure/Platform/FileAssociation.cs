namespace Keel.Infrastructure.Platform;

/// <summary>
/// The <c>.keel</c> document type (PRD 8, PRD 14 D12): one definition for every OS. Windows registers it
/// per user from Velopack's install hooks (<see cref="Windows.WindowsFileAssociation"/>); macOS declares it
/// in Keel.app's Info.plist and Linux in the .deb's shared-mime-info file (build/package.sh).
/// </summary>
public static class FileAssociation
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".keel";

    /// <summary>Windows programmatic id.</summary>
    public const string ProgId = "Keel.Budget";

    /// <summary>MIME type (Linux) and the basis of the macOS type identifier.</summary>
    public const string MimeType = "application/vnd.keel.budget";

    /// <summary>Human-readable type name.</summary>
    public const string Description = "Keel budget file";

    /// <summary>
    /// The per-user registry values under <paramref name="classesRoot"/> (normally <c>Software\Classes</c> in
    /// HKEY_CURRENT_USER) that make double-clicking a <c>.keel</c> file start <paramref name="executable"/> with
    /// the file's path; a running Keel then receives it through the single-instance channel.
    /// </summary>
    /// <param name="executable">Full path of Keel.Desktop.exe.</param>
    /// <param name="classesRoot">Registry path of the classes root.</param>
    public static IReadOnlyList<RegistryValue> WindowsValues(string executable, string classesRoot = @"Software\Classes")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(classesRoot);
        var progId = classesRoot + @"\" + ProgId;
        return
        [
            new(classesRoot + @"\" + Extension, null, ProgId),
            new(classesRoot + @"\" + Extension, "Content Type", MimeType),
            new(classesRoot + @"\" + Extension + @"\OpenWithProgids", ProgId, string.Empty),
            new(progId, null, Description),
            new(progId + @"\DefaultIcon", null, $"\"{executable}\",0"),
            new(progId + @"\shell\open\command", null, $"\"{executable}\" \"%1\""),
        ];
    }

    /// <summary>The keys removed on uninstall (the extension key only when it still points to Keel).</summary>
    /// <param name="classesRoot">Registry path of the classes root.</param>
    public static IReadOnlyList<string> WindowsKeys(string classesRoot = @"Software\Classes") =>
        [classesRoot + @"\" + ProgId, classesRoot + @"\" + Extension];
}

/// <summary>A registry value to write: key path, value name (null = default value) and string data.</summary>
/// <param name="Key">Key path below the hive.</param>
/// <param name="Name">Value name; null for the key's default value.</param>
/// <param name="Data">String data.</param>
public sealed record RegistryValue(string Key, string? Name, string Data);
