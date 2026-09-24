using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Settings;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

public sealed class RenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public void Every_screen_renders_in_light_and_dark_without_binding_or_resource_errors()
    {
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var themes = _host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var item in shell.AllItems)
            {
                item.NavigateCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                frame.ShouldNotBeNull();
                frame.PixelSize.Width.ShouldBeGreaterThan(0);

                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    frame.Save(Path.Combine(outputDir, $"{item.PageType.Name.Replace("ViewModel", string.Empty, StringComparison.Ordinal)}-{theme}.png"));
                }
            }
        }

        // Every sidebar icon resolved to a geometry.
        window.GetVisualDescendants().OfType<Icon>().ShouldAllBe(i => i.Data != null);

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Register_renders_fixture_data_in_light_and_dark()
    {
        var fixture = await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(
            _host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(5_000, Seed: 42), CancellationToken.None));
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var register = _host.Get<AccountsViewModel>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var (name, target) in new (string, Guid?)[] { ("Register", fixture.Accounts["Visa Rewards"]), ("AllAccounts", null), ("Tracking", fixture.Accounts["Brokerage"]) })
            {
                if (target is { } id)
                {
                    shell.OpenAccount(id);
                }
                else
                {
                    shell.AccountItems[0].NavigateCommand.Execute(null);
                }

                await register.SettleAsync();
                if (name == "Register")
                {
                    // Show the inline editor and a split row as well.
                    var split = register.Rows.LoadedRows.FirstOrDefault(r => r.IsSplit);
                    if (split is not null)
                    {
                        split.IsExpanded = true;
                    }

                    await register.NewTransactionAsync();
                    register.Editor!.Payee = "Trader Joe's";
                    register.Editor.Outflow = 4_250;
                    register.StartReconcile();
                    await register.SettleAsync();
                }

                using var frame = window.CaptureRenderedFrame()!;
                frame.PixelSize.Width.ShouldBeGreaterThan(0);
                register.RowCount.ShouldBeGreaterThan(0);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    frame.Save(Path.Combine(outputDir, $"{name}-{theme}.png"));
                }

                register.CancelEdit();
                register.Reconcile?.CancelCommand.Execute(null);
            }

            // The add-account dialog over the shell.
            shell.AddAccountCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            using (var dialogFrame = window.CaptureRenderedFrame()!)
            {
                if (!string.IsNullOrEmpty(outputDir))
                {
                    dialogFrame.Save(Path.Combine(outputDir, $"AddAccountDialog-{theme}.png"));
                }
            }

            shell.Dialogs.Current!.CancelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Budget_renders_fixture_data_in_light_and_dark()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        await Task.Run(async () =>
        {
            var budget = _host.Get<Keel.Application.Budget.IBudgetService>();
            var transactions = _host.Get<Keel.Application.Ledger.ITransactionService>();
            await budget.SetTargetAsync(new Keel.Application.Budget.TargetDto(ledger.Groceries, Keel.Domain.TargetType.MonthlySpending, 500_00), CancellationToken.None);
            await budget.SetTargetAsync(new Keel.Application.Budget.TargetDto(ledger.Rent, Keel.Domain.TargetType.MonthlySetAside, 1_500_00), CancellationToken.None);
            await transactions.SaveAsync(new Keel.Application.Ledger.SaveTransactionRequest(null, ledger.Checking, new DateOnly(2026, 8, 12), -42_50, "Bistro", ledger.Dining, null), CancellationToken.None);
        });
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var budgetVm = _host.Get<BudgetViewModel>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        shell.PrimaryItems.Single(i => i.PageType == typeof(BudgetViewModel)).NavigateCommand.Execute(null);
        await budgetVm.SettleAsync();
        budgetVm.GoToMonth(BudgetTestLedger.August);
        await budgetVm.SettleAsync();

        void Save(string name, AppTheme theme)
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            frame.PixelSize.Width.ShouldBeGreaterThan(0);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                frame.Save(Path.Combine(outputDir, $"{name}-{theme}.png"));
            }
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            budgetVm.Select(budgetVm.Row(ledger.Groceries), Keel.Desktop.ViewModels.Budget.BudgetColumn.Assigned);
            await budgetVm.SettleAsync();
            Save("BudgetGrid", theme);

            budgetVm.ExplainReadyToAssign();
            await budgetVm.SettleAsync();
            Save("BudgetReadyToAssign", theme);

            await Task.WhenAny(budgetVm.MoveMoneyAsync(ledger.Groceries), Task.Delay(200));
            await budgetVm.SettleAsync();
            Save("BudgetMoveMoney", theme);
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await budgetVm.SettleAsync();

            var picker = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MonthPickerButton");
            picker.Flyout!.ShowAt(picker);
            await budgetVm.SettleAsync();
            Save("BudgetMonthPicker", theme);
            picker.Flyout.Hide();

            var manage = budgetVm.ManageCategoriesAsync();
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, "manage dialog");
            await budgetVm.SettleAsync();
            Save("BudgetManageCategories", theme);
            shell.Dialogs.Current!.ConfirmCommand.Execute(null);
            await manage;
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void Light_and_dark_frames_differ()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var themes = _host.Get<ThemeService>();

        themes.SetTheme(AppTheme.Light);
        Dispatcher.UIThread.RunJobs();
        using var light = window.CaptureRenderedFrame()!;
        var lightPixel = CenterPixel(light);

        themes.SetTheme(AppTheme.Dark);
        Dispatcher.UIThread.RunJobs();
        using var dark = window.CaptureRenderedFrame()!;
        var darkPixel = CenterPixel(dark);

        // The page background is near-white in light and near-black in dark.
        Luminance(lightPixel).ShouldBeGreaterThan(200);
        Luminance(darkPixel).ShouldBeLessThan(60);

        themes.SetTheme(AppTheme.System);
        window.Close();
    }

    private static byte[] CenterPixel(WriteableBitmap bitmap)
    {
        using var locked = bitmap.Lock();
        var x = bitmap.PixelSize.Width - 40;
        var y = bitmap.PixelSize.Height / 2;
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address + (y * locked.RowBytes) + (x * 4), bytes, 0, 4);
        return bytes; // BGRA
    }

    private static double Luminance(byte[] bgra) => (0.0722 * bgra[0]) + (0.7152 * bgra[1]) + (0.2126 * bgra[2]);
}
