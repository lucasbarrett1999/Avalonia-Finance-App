using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Settings;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.ViewModels.FirstRun;
using Keel.Desktop.Views;
using Keel.Desktop.Views.FirstRun;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Keel.Desktop.Tests;

/// <summary>The first-run setup (PRD 9.10, normative), driven through the real window.</summary>
public sealed class FirstRunTests(ITestOutputHelper output) : IDisposable
{
    private readonly FakeFileDialogs _files = new();
    private TestHost? _host;

    private TestHost Host => _host ??= TestHost.CreateFirstRun(services => services.AddSingleton<IFileDialogs>(_files));

    public void Dispose() => _host?.Dispose();

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = Host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<ShellViewModel> SwitchedAsync(ShellWindow window, ShellViewModel previous)
    {
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, previous), "the window moved to the new session");
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return shell;
    }

    [AvaloniaFact]
    public async Task A_fresh_user_creates_a_file_picks_a_template_adds_an_account_and_lands_on_budget()
    {
        var clock = Stopwatch.StartNew();
        var (window, welcomeShell) = await ShowAsync();

        // Nothing was created silently: no budget file until the user chooses.
        Host.Get<AppSession>().BudgetFile.ShouldBeNull();
        File.Exists(Host.DataDirectory.DefaultBudgetFile).ShouldBeFalse();

        // 1. Welcome: create a new budget file (Enter in the name box creates it).
        var welcome = welcomeShell.FirstRun.ShouldNotBeNull();
        welcome.IsWelcome.ShouldBeTrue();
        welcomeShell.IsFirstRunVisible.ShouldBeTrue();
        var view = window.GetVisualDescendants().OfType<FirstRunView>().Single();
        view.Named<StackPanel>("WelcomeStep").IsEffectivelyVisible.ShouldBeTrue();
        view.Named<Button>("OpenExistingButton").IsEffectivelyVisible.ShouldBeTrue();
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() == view.Named<TextBox>("FileNameBox"), "focus in the file name");
        view.Named<TextBox>("FileNameBox").Text = "Household";
        window.Press(PhysicalKey.Enter);
        await welcome.Running;

        var shell = await SwitchedAsync(window, welcomeShell);
        var file = Path.Combine(Host.DataDirectory.BudgetsDirectory, "Household.keel");
        File.Exists(file).ShouldBeTrue();
        Host.Current<AppSession>().BudgetFile!.Path.ShouldBe(file);
        Host.Current<IAppSettingsStore>().Current.LastBudgetFile.ShouldBe(file);

        // 2. Starter template.
        var setup = shell.FirstRun.ShouldNotBeNull();
        setup.IsTemplate.ShouldBeTrue();
        setup.StepText.ShouldContain("2");
        view = window.GetVisualDescendants().OfType<FirstRunView>().Single();
        view.Named<StackPanel>("TemplateStep").IsEffectivelyVisible.ShouldBeTrue();
        setup.Templates.Count.ShouldBeGreaterThan(1);
        setup.Templates[^1].Template.ShouldBeNull(); // "Start empty" is offered
        setup.SelectedTemplate = setup.Templates[0];
        ImportDialogTests.Click(window, view.Named<Button>("TemplateContinueButton"));
        await setup.Running;
        setup.IsAccount.ShouldBeTrue();
        var categories = await Task.Run(() => Host.Current<ICategoryService>().GetCategoriesAsync(includeHidden: false, CancellationToken.None));
        categories.Count(c => !c.IsSystem).ShouldBe(setup.Templates[0].Template!.Groups.Sum(g => g.Categories.Count));

        // 3. First account with its balance; the bank option explains itself and links to Connections.
        view.Named<Button>("ConnectBankButton").IsEffectivelyVisible.ShouldBeTrue();
        ImportDialogTests.Click(window, view.Named<Button>("BankInfoButton"));
        view.Named<TextBlock>("BankInfoText").IsEffectivelyVisible.ShouldBeTrue();
        view.Named<TextBox>("AccountNameBox").Text = "Everyday checking";
        setup.Balance = 2_345_67;
        ImportDialogTests.Click(window, view.Named<Button>("AddAccountButton"));
        await setup.Running;
        setup.Error.ShouldBeNull();
        setup.CreatedAccount.ShouldNotBeNull().Name.ShouldBe("Everyday checking");

        // 4. Budget, with Ready to Assign showing the opening balance.
        shell.IsFirstRunVisible.ShouldBeFalse();
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        var budget = Host.Current<BudgetViewModel>();
        await budget.SettleAsync();
        budget.ReadyToAssign.ShouldBe(2_345_67);
        window.Budget().Named<TextBlock>("ReadyToAssignText").Text.ShouldBe(budget.ReadyToAssignText);
        Host.Current<IAppSettingsStore>().Current.FirstRunCompleted.ShouldBeTrue();

        // The Home checklist: file, categories and account done; assigning is next.
        var home = Host.Current<HomeViewModel>();
        shell.PrimaryItems.Single(i => i.PageType == typeof(HomeViewModel)).NavigateCommand.Execute(null);
        await SettleAsync(() => home.Loading);
        home.ShowChecklist.ShouldBeTrue();
        home.SetupSteps.Count.ShouldBe(4);
        home.SetupSteps.Select(s => s.IsDone).ShouldBe([true, true, true, false]);
        home.SetupCompleted.ShouldBe(3);
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SetupChecklistCard").IsEffectivelyVisible.ShouldBeTrue();

        // Assign the whole balance: the fourth step ticks itself off.
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var category = categories.First(c => !c.IsSystem);
        await Task.Run(() => Host.Current<IBudgetService>().AssignAsync(category.Id, month, 2_345_67, CancellationToken.None));
        await SettleAsync(() => home.Loading);
        await UiTestHelpers.WaitUntilAsync(() => home.IsSetupComplete, "all four steps done");
        home.SetupCompleted.ShouldBe(4);

        // Dismissing hides the card for this file.
        ImportDialogTests.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DismissChecklistButton"));
        home.ShowChecklist.ShouldBeFalse();
        output.WriteLine($"First-run flow completed headlessly in {clock.ElapsedMilliseconds} ms");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Open_existing_ends_the_setup_in_that_file()
    {
        // A budget file the user already has.
        var existing = Path.Combine(Host.Root, "elsewhere", "Mine.keel");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        var (window, shell) = await ShowAsync();
        _files.OpenBudgetFiles.Enqueue(null); // first attempt cancelled: nothing happens
        var welcome = shell.FirstRun.ShouldNotBeNull();
        await welcome.OpenExistingAsync();
        window.DataContext.ShouldBeSameAs(shell);
        welcome.IsWelcome.ShouldBeTrue();

        // Create the file through a throwaway session so it is a real, migrated budget file.
        using (var other = TestHost.Create())
        {
            await Task.Run(() => other.Get<Keel.Application.Files.IBudgetFileService>().OpenOrCreateAsync(existing, CancellationToken.None));
        }

        _files.OpenBudgetFiles.Enqueue(existing);
        ImportDialogTests.Click(window, window.GetVisualDescendants().OfType<FirstRunView>().Single().Named<Button>("OpenExistingButton"));
        await welcome.Running;
        var next = await SwitchedAsync(window, shell);
        next.IsFirstRunVisible.ShouldBeFalse();
        Host.Current<AppSession>().BudgetFile!.Path.ShouldBe(existing);
        Host.Current<IAppSettingsStore>().Current.FirstRunCompleted.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Skipping_the_account_goes_home_and_connect_a_bank_opens_connections()
    {
        var (window, welcomeShell) = await ShowAsync();
        await welcomeShell.FirstRun!.CreateAsync();
        var shell = await SwitchedAsync(window, welcomeShell);
        var setup = shell.FirstRun!;
        setup.SelectedTemplate = setup.Templates[^1]; // start empty
        await setup.ApplyTemplateAsync();
        setup.IsAccount.ShouldBeTrue();
        setup.ConnectBank();
        shell.IsFirstRunVisible.ShouldBeFalse();
        shell.CurrentPage.ShouldBeOfType<SettingsViewModel>();
        Host.Current<SettingsViewModel>().RequestedSection.ShouldBe("Connections");

        // The checklist keeps the open steps: no categories, no account yet.
        var home = Host.Current<HomeViewModel>();
        shell.PrimaryItems.Single(i => i.PageType == typeof(HomeViewModel)).NavigateCommand.Execute(null);
        await SettleAsync(() => home.Loading);
        home.SetupSteps.Select(s => s.IsDone).ShouldBe([true, false, false, false]);
        home.SetupSteps[2].ShowAction.ShouldBeTrue();

        // A later launch never shows the setup again.
        Host.Current<IAppSettingsStore>().Current.FirstRunCompleted.ShouldBeTrue();
        Host.Current<BudgetFileStartup>().IsFirstRun.ShouldBeFalse();
        window.Close();
    }

    private static async Task SettleAsync(Func<Task> loading)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await loading();
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
