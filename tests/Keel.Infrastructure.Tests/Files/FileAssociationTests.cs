using Keel.Application.Files;
using Keel.Infrastructure.Platform;
using Keel.Infrastructure.Platform.Windows;

namespace Keel.Infrastructure.Tests.Files;

/// <summary>The .keel document type (PRD 8, PRD 14 D12).</summary>
public sealed class FileAssociationTests
{
    [Fact]
    public void The_extension_matches_the_budget_file_service()
    {
        FileAssociation.Extension.ShouldBe(IBudgetFileService.Extension);
    }

    [Fact]
    public void Windows_values_open_the_file_with_the_installed_executable()
    {
        var values = FileAssociation.WindowsValues(@"C:\Users\me\AppData\Local\Keel\current\Keel.Desktop.exe");
        values.ShouldContain(new RegistryValue(@"Software\Classes\.keel", null, "Keel.Budget"));
        values.ShouldContain(new RegistryValue(@"Software\Classes\Keel.Budget\shell\open\command", null, "\"C:\\Users\\me\\AppData\\Local\\Keel\\current\\Keel.Desktop.exe\" \"%1\""));
        values.ShouldContain(v => v.Key == @"Software\Classes\Keel.Budget\DefaultIcon");
        values.ShouldAllBe(v => v.Key.StartsWith(@"Software\Classes\", StringComparison.Ordinal));
        FileAssociation.WindowsKeys().ShouldBe([@"Software\Classes\Keel.Budget", @"Software\Classes\.keel"]);
    }

    [Fact]
    public void Registers_and_removes_the_type_in_the_registry_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The registry exists only on Windows; the values above are checked on every OS.
            return;
        }

        var root = @"Software\KeelTests\" + Guid.NewGuid().ToString("N") + @"\Classes";
        try
        {
            WindowsFileAssociation.Register(@"C:\Keel\Keel.Desktop.exe", root);
            WindowsFileAssociation.OpenCommand(root).ShouldBe("\"C:\\Keel\\Keel.Desktop.exe\" \"%1\"");
            WindowsFileAssociation.Unregister(root);
            WindowsFileAssociation.OpenCommand(root).ShouldBeNull();
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root[..root.LastIndexOf('\\')], throwOnMissingSubKey: false);
        }
    }
}
