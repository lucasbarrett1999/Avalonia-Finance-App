using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Keel.Infrastructure.Platform.Windows;

/// <summary>
/// Registers the <c>.keel</c> file type for the current user (HKEY_CURRENT_USER, no elevation), from
/// Velopack's install and update hooks, and removes it on uninstall (PRD 8, ADR 0083).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsFileAssociation
{
    private const int ShcneAssocChanged = 0x08000000;

    /// <summary>Writes <see cref="FileAssociation.WindowsValues"/> and tells Explorer about it.</summary>
    /// <param name="executable">Full path of Keel.Desktop.exe.</param>
    /// <param name="classesRoot">Registry path of the classes root (tests use a scratch key).</param>
    public static void Register(string executable, string classesRoot = @"Software\Classes")
    {
        foreach (var value in FileAssociation.WindowsValues(executable, classesRoot))
        {
            using var key = Registry.CurrentUser.CreateSubKey(value.Key, writable: true);
            key.SetValue(value.Name ?? string.Empty, value.Data, RegistryValueKind.String);
        }

        Notify();
    }

    /// <summary>Removes Keel's keys; the extension key only when it still names Keel.</summary>
    /// <param name="classesRoot">Registry path of the classes root.</param>
    public static void Unregister(string classesRoot = @"Software\Classes")
    {
        Registry.CurrentUser.DeleteSubKeyTree(classesRoot + @"\" + FileAssociation.ProgId, throwOnMissingSubKey: false);
        var extension = classesRoot + @"\" + FileAssociation.Extension;
        using (var key = Registry.CurrentUser.OpenSubKey(extension))
        {
            if (key?.GetValue(string.Empty) as string != FileAssociation.ProgId)
            {
                Notify();
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(extension, throwOnMissingSubKey: false);
        Notify();
    }

    /// <summary>The command registered to open <c>.keel</c> files, or null.</summary>
    /// <param name="classesRoot">Registry path of the classes root.</param>
    public static string? OpenCommand(string classesRoot = @"Software\Classes")
    {
        using var key = Registry.CurrentUser.OpenSubKey(classesRoot + @"\" + FileAssociation.ProgId + @"\shell\open\command");
        return key?.GetValue(string.Empty) as string;
    }

    private static void Notify() => SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
