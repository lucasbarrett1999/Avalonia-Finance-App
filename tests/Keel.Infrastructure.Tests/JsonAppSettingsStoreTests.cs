using Keel.Application.Settings;
using Keel.Infrastructure.Settings;

namespace Keel.Infrastructure.Tests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Missing_file_yields_defaults_without_writing()
    {
        var path = _temp.File("settings.json");
        var store = new JsonAppSettingsStore(path);

        store.Current.Theme.ShouldBe(AppTheme.System);
        store.Current.LastBudgetFile.ShouldBeNull();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void Round_trips_theme_last_file_and_window_state()
    {
        var path = _temp.File("settings.json");
        var placement = new WindowPlacement(1280, 800, 40, 60, IsMaximized: false, SidebarWidth: 240, IsSidebarCollapsed: true);
        new JsonAppSettingsStore(path).Update(s => (s with { Theme = AppTheme.Dark, LastBudgetFile = "/tmp/Budget.keel" })
            .WithWindowPlacement("1:0,0,1920,1080@1", placement));

        var reloaded = new JsonAppSettingsStore(path).Current;
        reloaded.Theme.ShouldBe(AppTheme.Dark);
        reloaded.LastBudgetFile.ShouldBe("/tmp/Budget.keel");
        reloaded.WindowPlacements["1:0,0,1920,1080@1"].ShouldBe(placement);
        File.ReadAllText(path).ShouldContain("\"theme\": \"Dark\"");
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task Async_update_persists()
    {
        var path = _temp.File("nested/settings.json");
        await new JsonAppSettingsStore(path).UpdateAsync(s => s with { Theme = AppTheme.Light }, CancellationToken.None);
        new JsonAppSettingsStore(path).Current.Theme.ShouldBe(AppTheme.Light);
    }

    [Fact]
    public void Corrupt_file_yields_defaults_and_is_left_alone()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(path, "{ not json");

        var current = new JsonAppSettingsStore(path).Current;
        current.Theme.ShouldBe(AppTheme.System);
        current.LastBudgetFile.ShouldBeNull();
        current.WindowPlacements.ShouldBeEmpty();
        File.ReadAllText(path).ShouldBe("{ not json");
    }
}
