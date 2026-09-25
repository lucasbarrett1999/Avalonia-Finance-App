using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;

namespace Keel.Desktop.Tests;

/// <summary>Accent, density, reduced motion and formats (F-SET-2, PRD 9.11).</summary>
public sealed class AppearanceTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static Color Accent(Control control, ThemeVariant theme) =>
        control.TryFindResource("Keel.Accent", theme, out var value) && value is ISolidColorBrush brush ? brush.Color : default;

    [AvaloniaFact]
    public async Task Accent_density_and_reduced_motion_apply_to_the_window_and_are_saved()
    {
        var (window, _) = await ShowAsync();
        var vm = _host.Get<AppearanceSettingsViewModel>();
        Accent(window, ThemeVariant.Light).ShouldBe(AppearanceService.Accents[AppAccent.Teal].Light);

        try
        {
            vm.SelectedAccent = vm.Accents.Single(a => a.Accent == AppAccent.Violet);
            Accent(window, ThemeVariant.Light).ShouldBe(AppearanceService.Accents[AppAccent.Violet].Light);
            Accent(window, ThemeVariant.Dark).ShouldBe(AppearanceService.Accents[AppAccent.Violet].Dark);
            _host.Get<IAppSettingsStore>().Current.Accent.ShouldBe(AppAccent.Violet);
        }
        finally
        {
            // The accent lives in app-wide resources: put Keel's teal back for the next test.
            AppearanceService.ApplyAccent(AppAccent.Teal);
        }

        Accent(window, ThemeVariant.Light).ShouldBe(AppearanceService.Accents[AppAccent.Teal].Light);

        window.Classes.Contains("compact").ShouldBeFalse();
        vm.IsCompact = true;
        window.Classes.Contains("compact").ShouldBeTrue();
        _host.Get<IAppSettingsStore>().Current.Density.ShouldBe(UiDensity.Compact);
        vm.IsComfortable = true;
        window.Classes.Contains("compact").ShouldBeFalse();

        // Reduce motion: an explicit choice wins over the OS; transitions are removed.
        vm.MotionIndex = 1;
        window.Classes.Contains("reduceMotion").ShouldBeTrue();
        _host.Get<IAppSettingsStore>().Current.ReduceMotion.ShouldBe(true);
        // A transition from an app style (as Keel's and the theme's are) is removed by the reduceMotion class.
        var style = new Style(x => x.OfType<Border>().Class("motionProbe"))
        {
            Setters = { new Setter(Animatable.TransitionsProperty, new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) } }) },
        };
        Avalonia.Application.Current!.Styles.Insert(0, style);
        try
        {
            var probe = new Border { Classes = { "motionProbe" } };
            ((Panel)window.FindControl<ShellView>("Shell")!.Content!).Children.Add(probe);
            Dispatcher.UIThread.RunJobs();
            probe.Transitions.ShouldBeNull();
            vm.MotionIndex = 2;
            Dispatcher.UIThread.RunJobs();
            probe.Transitions.ShouldNotBeNull().Count.ShouldBe(1);
        }
        finally
        {
            Avalonia.Application.Current.Styles.Remove(style);
        }

        vm.MotionIndex = 2;
        window.Classes.Contains("reduceMotion").ShouldBeFalse();
        vm.MotionIndex = 0;
        _host.Get<IAppSettingsStore>().Current.ReduceMotion.ShouldBeNull();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_format_override_reformats_after_reopening_and_following_the_os_restores_it()
    {
        var original = CultureInfo.CurrentCulture;
        var (window, shell) = await ShowAsync();
        var vm = _host.Get<AppearanceSettingsViewModel>();
        vm.Cultures[0].Name.ShouldBeNull(); // follow the OS
        try
        {
            vm.SelectedCulture = vm.Cultures.Single(c => c.Name == "de-DE");
            vm.FormatSample.ShouldContain("1.234.567,89");
            var reopened = await SwitchedAsync(window, shell);
            CultureInfo.CurrentCulture.Name.ShouldBe("de-DE");
            _host.Current<IAppSettingsStore>().Current.FormatCulture.ShouldBe("de-DE");
            reopened.StatusMessage.ShouldBe(Keel.Desktop.Resources.Strings.Settings_FormatApplied);

            var next = _host.Current<AppearanceSettingsViewModel>();
            next.SelectedCulture.Name.ShouldBe("de-DE");
            next.SelectedCulture = next.Cultures[0];
            await SwitchedAsync(window, reopened);
            _host.Current<IAppSettingsStore>().Current.FormatCulture.ShouldBeNull();
            CultureInfo.CurrentCulture.ShouldBe(LocaleService.OsCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.DefaultThreadCurrentCulture = null;
        }

        window.Close();
    }

    private static async Task<ShellViewModel> SwitchedAsync(ShellWindow window, ShellViewModel previous)
    {
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, previous), "the window moved to the new session");
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return shell;
    }
}

/// <summary>Format cultures (F-SET-2).</summary>
public sealed class LocaleServiceTests
{
    [Fact]
    public void Unknown_or_empty_culture_names_follow_the_os()
    {
        var os = CultureInfo.GetCultureInfo("en-GB");
        LocaleService.Resolve(null, os).ShouldBe(os);
        LocaleService.Resolve("", os).ShouldBe(os);
        LocaleService.Resolve("xx-NOT-A-CULTURE-!!", os).ShouldBe(os);
        LocaleService.Resolve("fr-FR", os).Name.ShouldBe("fr-FR");
        LocaleService.Sample(CultureInfo.GetCultureInfo("en-US")).ShouldContain("9/24/2026");
    }
}

/// <summary>Single instance and command-line file open (PRD 8).</summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keel-desktop-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_second_launch_hands_its_file_to_the_first_and_a_bare_launch_just_activates()
    {
        using var first = SingleInstance.TryAcquire(_root).ShouldNotBeNull();
        SingleInstance.TryAcquire(_root).ShouldBeNull(); // the lock is held

        var received = new List<string?>();
        var signal = new SemaphoreSlim(0);
        first.Listen(path =>
        {
            lock (received)
            {
                received.Add(path);
            }

            signal.Release();
        });

        var file = Path.Combine(_root, "budgets", "Family.keel");
        SingleInstance.TrySend(_root, file).ShouldBeTrue();
        (await signal.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        SingleInstance.TrySend(_root, null).ShouldBeTrue();
        (await signal.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        received.ShouldBe([file, null]);

        // Another data directory is another instance.
        using var other = SingleInstance.TryAcquire(_root + "-other").ShouldNotBeNull();
        SingleInstance.PipeNameFor(_root).ShouldNotBe(SingleInstance.PipeNameFor(_root + "-other"));
    }

    [Fact]
    public void Nobody_listening_means_the_launch_starts_normally()
    {
        SingleInstance.TrySend(_root, null, timeoutMs: 200).ShouldBeFalse();
    }

    [Theory]
    [InlineData(new[] { "--verbose", "/home/me/Budget.keel" }, "/home/me/Budget.keel")]
    [InlineData(new[] { "file:///home/me/My%20Budget.KEEL" }, "/home/me/My Budget.KEEL")]
    [InlineData(new[] { "notes.txt" }, null)]
    [InlineData(new string[0], null)]
    public void The_first_keel_argument_is_the_file_to_open(string[] args, string? expected)
    {
        var result = LaunchArguments.BudgetFile(args);
        if (expected is null)
        {
            result.ShouldBeNull();
        }
        else
        {
            result.ShouldBe(Path.GetFullPath(expected));
        }
    }
}
