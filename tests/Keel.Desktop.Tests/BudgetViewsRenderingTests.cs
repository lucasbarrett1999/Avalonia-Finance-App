using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.Views;
using Keel.Domain;

namespace Keel.Desktop.Tests;

/// <summary>
/// M9a screens in light and dark, at the default window size and at 960 × 540 (1080p at 200%): the three-month
/// grid, the Flex view, a Flex drill-down and the tag pickers, without binding or resource errors. With
/// KEEL_SCREENSHOT_DIR set the frames are saved for review.
/// </summary>
public sealed class BudgetViewsRenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static async Task SaveAsync(Window window, string name)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        using var frame = window.CaptureRenderedFrame()!;
        frame.PixelSize.Width.ShouldBeGreaterThan(0);
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            frame.Save(Path.Combine(outputDir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task Three_month_and_flex_views_render_in_light_and_dark_at_both_sizes()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        await Task.Run(async () =>
        {
            var budget = _host.Get<IBudgetService>();
            var categories = _host.Get<ICategoryService>();
            var transactions = _host.Get<Keel.Application.Ledger.ITransactionService>();
            await budget.SetTargetAsync(new TargetDto(ledger.Rent, TargetType.MonthlySetAside, 1_500_00), CancellationToken.None);
            var insurance = await categories.CreateCategoryAsync("Bills", "Car insurance", CancellationToken.None);
            await budget.SetTargetAsync(new TargetDto(insurance.Id, TargetType.SavingsBalanceByDate, 1_200_00, new DateOnly(2027, 6, 1)), CancellationToken.None);
            await budget.AssignAsync(insurance.Id, BudgetTestLedger.August, 100_00, CancellationToken.None);
            await budget.AssignAsync(ledger.Dining, BudgetTestLedger.August, 150_00, CancellationToken.None);
            await transactions.SaveAsync(new Keel.Application.Ledger.SaveTransactionRequest(null, ledger.Checking, new DateOnly(2026, 9, 1), 3_000_00, "Employer", Keel.Domain.Entities.SystemIds.ReadyToAssignCategory, null), CancellationToken.None);
            await transactions.SaveAsync(new Keel.Application.Ledger.SaveTransactionRequest(null, ledger.Checking, new DateOnly(2026, 9, 3), -61_20, "Grocer", ledger.Groceries, null), CancellationToken.None);
            await budget.AssignAsync(ledger.Groceries, new DateOnly(2026, 9, 1), 450_00, CancellationToken.None);
            await budget.AssignAsync(ledger.Rent, new DateOnly(2026, 9, 1), 1_500_00, CancellationToken.None);
            await transactions.SaveAsync(new Keel.Application.Ledger.SaveTransactionRequest(null, ledger.Checking, new DateOnly(2026, 8, 12), -42_50, "Bistro", ledger.Dining, null), CancellationToken.None);
        });

        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var vm = _host.Get<BudgetViewModel>();
        shell.NavigateTo<BudgetViewModel>();
        await vm.SettleAsync();
        vm.GoToMonth(BudgetTestLedger.August);
        await vm.SettleAsync();

        foreach (var (width, height, size) in new[] { (1440.0, 900.0, "wide"), (960.0, 540.0, "narrow") })
        {
            window.Width = width;
            window.Height = height;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                themes.SetTheme(theme);
                vm.SetFlexView(false);
                vm.SetThreeMonths(true);
                vm.GoToMonth(BudgetTestLedger.August);
                vm.IsInspectorOpen = size == "wide";
                await vm.SettleAsync();
                vm.Select(vm.Row(ledger.Groceries), BudgetColumn.Assigned);
                vm.MoveCursor(0, 3);                                   // September's Assigned
                await vm.SettleAsync();
                await SaveAsync(window, $"M9a-BudgetThreeMonths-{size}-{theme}");

                vm.SetFlexView(true);
                await vm.SettleAsync();
                await SaveAsync(window, $"M9a-BudgetFlex-{size}-{theme}");

                vm.Flex.ShowFlexCommand.Execute(null);
                await vm.SettleAsync();
                await SaveAsync(window, $"M9a-BudgetFlexFiltered-{size}-{theme}");
                vm.ClearFlexFilter();

                vm.IsInspectorOpen = true;
                vm.Select(vm.Row(ledger.Dining), BudgetColumn.Name);
                await vm.SettleAsync();
                await SaveAsync(window, $"M9a-BudgetInspectorFlexTag-{size}-{theme}");

                var manage = vm.ManageCategoriesAsync();
                await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ManageCategoriesDialogViewModel, "manage dialog");
                await SaveAsync(window, $"M9a-BudgetManageFlexTags-{size}-{theme}");
                shell.Dialogs.Current!.ConfirmCommand.Execute(null);
                await manage;
                await vm.SettleAsync();
            }
        }

        vm.SetThreeMonths(false);
        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
