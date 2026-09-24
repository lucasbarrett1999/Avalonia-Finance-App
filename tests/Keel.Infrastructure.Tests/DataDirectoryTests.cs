using Keel.Infrastructure.Files;

namespace Keel.Infrastructure.Tests;

public class DataDirectoryTests
{
    private const string Home = "/home/priya";

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Windows_uses_appdata()
    {
        DataDirectory.ResolveDefaultRoot(DataDirectoryPlatform.Windows, Env(("APPDATA", "C:/Users/priya/AppData/Roaming")), Home)
            .ShouldBe(Path.Combine("C:/Users/priya/AppData/Roaming", "Keel"));
    }

    [Fact]
    public void Windows_falls_back_to_roaming_under_home()
    {
        DataDirectory.ResolveDefaultRoot(DataDirectoryPlatform.Windows, Env(), Home)
            .ShouldBe(Path.Combine(Home, "AppData", "Roaming", "Keel"));
    }

    [Fact]
    public void MacOS_uses_application_support()
    {
        DataDirectory.ResolveDefaultRoot(DataDirectoryPlatform.MacOS, Env(), "/Users/priya")
            .ShouldBe(Path.Combine("/Users/priya", "Library", "Application Support", "Keel"));
    }

    [Fact]
    public void Linux_uses_local_share_keel()
    {
        DataDirectory.ResolveDefaultRoot(DataDirectoryPlatform.Linux, Env(), Home)
            .ShouldBe(Path.Combine(Home, ".local", "share", "keel"));
    }

    [Fact]
    public void Linux_honors_absolute_xdg_data_home()
    {
        DataDirectory.ResolveDefaultRoot(DataDirectoryPlatform.Linux, Env(("XDG_DATA_HOME", "/data/xdg")), Home)
            .ShouldBe(Path.Combine("/data/xdg", "keel"));
        DataDirectory.ResolveDefaultRoot(DataDirectoryPlatform.Linux, Env(("XDG_DATA_HOME", "relative/path")), Home)
            .ShouldBe(Path.Combine(Home, ".local", "share", "keel"));
    }

    [Fact]
    public void Lays_out_the_tree_from_PRD_7_4()
    {
        using var temp = new TempDirectory();
        var dir = new DataDirectory(temp.Path);
        dir.EnsureCreated();

        dir.SettingsFile.ShouldBe(Path.Combine(temp.Path, "settings.json"));
        dir.DefaultBudgetFile.ShouldBe(Path.Combine(temp.Path, "budgets", "Default.keel"));
        dir.BackupsDirectory.ShouldBe(Path.Combine(temp.Path, "budgets", "backups"));
        dir.SecretsDirectory.ShouldBe(Path.Combine(temp.Path, "secrets"));
        dir.AttachmentsDirectoryFor(dir.DefaultBudgetFile).ShouldBe(Path.Combine(temp.Path, "budgets", "Default.keel-attachments"));
        Directory.Exists(dir.LogsDirectory).ShouldBeTrue();
        Directory.Exists(dir.BackupsDirectory).ShouldBeTrue();
    }

    [Fact]
    public void Current_user_directory_is_absolute()
    {
        Path.IsPathRooted(DataDirectory.ForCurrentUser().Root).ShouldBeTrue();
    }
}
