using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Settings;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;
using Keel.Infrastructure.Settings;

namespace Keel.Desktop.Tests;

public sealed class ShellSmokeTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private ShellWindow ShowShell()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Single<T>(Visual root)
        where T : Visual => root.GetVisualDescendants().OfType<T>().ShouldHaveSingleItem();

    [AvaloniaFact]
    public void First_launch_creates_the_default_budget_file_and_opens_home()
    {
        var window = ShowShell();
        var shell = (ShellViewModel)window.DataContext!;

        File.Exists(_host.DataDirectory.DefaultBudgetFile).ShouldBeTrue();
        shell.FileName.ShouldBe("Default.keel");
        shell.StatusMessage.ShouldContain("Default.keel");
        window.Title.ShouldBe("Default.keel - Keel");
        new JsonAppSettingsStore(_host.DataDirectory.SettingsFile).Current.LastBudgetFile
            .ShouldBe(_host.DataDirectory.DefaultBudgetFile);

        shell.CurrentPage.ShouldBeOfType<HomeViewModel>();
        Single<HomeView>(window);
        shell.PrimaryItems[0].IsSelected.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public void Navigates_to_every_screen_and_shows_its_designed_empty_state()
    {
        var window = ShowShell();
        var shell = (ShellViewModel)window.DataContext!;
        var items = shell.AllItems.ToList();
        items.Select(i => i.Title).ShouldBe(["Home", "Budget", "Review", "Bills", "Goals", "Reports", "All accounts", "Settings"]);

        foreach (var item in items)
        {
            item.NavigateCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            shell.CurrentPage.ShouldNotBeNull();
            shell.CurrentPage.GetType().ShouldBe(item.PageType);
            items.Where(i => i.IsSelected).ShouldHaveSingleItem().ShouldBe(item);

            var host = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PageHost");
            var view = host.GetVisualDescendants().OfType<UserControl>().First();
            view.GetType().ShouldBe(ViewLocator.ViewTypeFor(item.PageType));
            view.DataContext.ShouldBeSameAs(shell.CurrentPage);

            var page = (PageViewModel)shell.CurrentPage;
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            texts.ShouldContain(page.Title);
            texts.ShouldNotContain(t => t != null && t.Contains("TODO", StringComparison.OrdinalIgnoreCase));
            if (page is not SettingsViewModel)
            {
                var empty = view.GetVisualDescendants().OfType<EmptyState>().ShouldHaveSingleItem();
                empty.Heading.ShouldBe(page.EmptyHeading);
                empty.Message.ShouldNotBeNullOrWhiteSpace();
                empty.Icon.ShouldNotBeNull();
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_sidebar_item_navigates()
    {
        var window = ShowShell();
        var shell = (ShellViewModel)window.DataContext!;
        var budgetButton = window.GetVisualDescendants().OfType<RadioButton>()
            .Single(r => r.DataContext is NavigationItemViewModel { PageType: var t } && t == typeof(BudgetViewModel));

        var center = budgetButton.TranslatePoint(new Point(budgetButton.Bounds.Width / 2, budgetButton.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        Single<BudgetView>(window);
        window.Close();
    }

    [AvaloniaFact]
    public void Toggles_the_theme_and_persists_it()
    {
        var window = ShowShell();
        var shell = (ShellViewModel)window.DataContext!;
        shell.NavigateTo<SettingsViewModel>();
        Dispatcher.UIThread.RunJobs();
        var settings = _host.Get<SettingsViewModel>();
        var app = Avalonia.Application.Current!;

        settings.IsDarkTheme = true;
        Dispatcher.UIThread.RunJobs();
        app.RequestedThemeVariant.ShouldBe(ThemeVariant.Dark);
        window.ActualThemeVariant.ShouldBe(ThemeVariant.Dark);
        new JsonAppSettingsStore(_host.DataDirectory.SettingsFile).Current.Theme.ShouldBe(AppTheme.Dark);

        // The radio button in the Settings view is bound two-way.
        var light = window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "ThemeLight");
        light.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        settings.Theme.ShouldBe(AppTheme.Light);
        window.ActualThemeVariant.ShouldBe(ThemeVariant.Light);
        new JsonAppSettingsStore(_host.DataDirectory.SettingsFile).Current.Theme.ShouldBe(AppTheme.Light);

        settings.IsSystemTheme = true;
        Dispatcher.UIThread.RunJobs();
        app.RequestedThemeVariant.ShouldBe(ThemeVariant.Default);
        new JsonAppSettingsStore(_host.DataDirectory.SettingsFile).Current.Theme.ShouldBe(AppTheme.System);
        window.Close();
    }

    [AvaloniaFact]
    public void Saved_theme_is_applied_on_startup()
    {
        _host.Get<IAppSettingsStore>().Update(s => s with { Theme = AppTheme.Dark });
        _host.Get<ThemeService>().ApplySaved();
        Avalonia.Application.Current!.RequestedThemeVariant.ShouldBe(ThemeVariant.Dark);
        _host.Get<ThemeService>().SetTheme(AppTheme.System);
    }

    [AvaloniaFact]
    public void Command_shortcut_focuses_search()
    {
        var window = ShowShell();
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox");
        search.IsFocused.ShouldBeFalse();

        var shortcuts = _host.Get<PlatformShortcuts>();
        window.KeyPressQwerty(PhysicalKey.F, shortcuts.CommandModifiers == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        search.IsFocused.ShouldBeTrue();
        ((ShellViewModel)window.DataContext!).SearchWatermark.ShouldContain(shortcuts.Format(shortcuts.Search));
        window.Close();
    }

    [AvaloniaFact]
    public void Undo_and_redo_start_disabled_and_sync_is_a_disabled_placeholder()
    {
        var window = ShowShell();
        var shell = (ShellViewModel)window.DataContext!;
        shell.UndoCommand.CanExecute(null).ShouldBeFalse();
        shell.RedoCommand.CanExecute(null).ShouldBeFalse();
        shell.SyncAllCommand.CanExecute(null).ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public void Sidebar_collapses_to_icons_and_window_state_is_persisted()
    {
        var window = ShowShell();
        var shell = (ShellViewModel)window.DataContext!;
        var sidebar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Sidebar");
        var label = sidebar.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("navLabel"));
        label.IsVisible.ShouldBeTrue();

        shell.ToggleSidebarCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        label.IsVisible.ShouldBeFalse();
        sidebar.Bounds.Width.ShouldBe(60);

        shell.ResizeSidebar(1_000);
        shell.SidebarWidth.ShouldBe(ShellViewModel.MaxSidebarWidth);
        shell.ResizeSidebar(260);

        window.Width = 1000;
        window.Height = 700;
        Dispatcher.UIThread.RunJobs();
        window.Close();

        var saved = new JsonAppSettingsStore(_host.DataDirectory.SettingsFile).Current.WindowPlacements.ShouldHaveSingleItem().Value;
        saved.Width.ShouldBe(1000);
        saved.Height.ShouldBe(700);
        saved.IsSidebarCollapsed.ShouldBeTrue();
        saved.SidebarWidth.ShouldBe(260);

        // A new window restores the placement.
        var reopened = _host.Get<ShellWindow>();
        reopened.Width.ShouldBe(1000);
        reopened.Height.ShouldBe(700);
        ((ShellViewModel)reopened.DataContext!).IsSidebarCollapsed.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Every_view_model_has_a_view()
    {
        var viewModels = typeof(App).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ViewModelBase).IsAssignableFrom(t) && t != typeof(ShellViewModel));
        foreach (var vm in viewModels)
        {
            ViewLocator.ViewTypeFor(vm).ShouldNotBeNull($"{vm.Name} has no view");
        }
    }
}
