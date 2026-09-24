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
    public async Task Import_dialogs_render_in_light_and_dark()
    {
        var accounts = _host.Get<Keel.Application.Accounts.IAccountService>();
        var checking = await Task.Run(() => accounts.CreateAccountAsync(new("Checking", Keel.Domain.AccountType.Checking, "USD", new DateOnly(2026, 8, 1), 250_000), CancellationToken.None));
        var savings = await Task.Run(() => accounts.CreateAccountAsync(new("Savings", Keel.Domain.AccountType.Savings, "USD", new DateOnly(2026, 8, 1), 0), CancellationToken.None));
        var imports = _host.Get<Keel.Application.Import.IImportService>();
        await Task.Run(() => imports.ImportTransactionsAsync(Keel.Domain.TransactionSource.File, new(checking.Id,
            [new(new DateOnly(2026, 8, 2), -1_000, "OLD COFFEE", ProviderTransactionId: "A1"), new(new DateOnly(2026, 8, 3), -2_000, "GAS", ProviderTransactionId: "A2")]), CancellationToken.None));
        await Task.Run(() => imports.ImportTransactionsAsync(Keel.Domain.TransactionSource.File, new(savings.Id, [new(new DateOnly(2026, 8, 6), 30_000, "FROM CHECKING")]), CancellationToken.None));
        await Task.Run(() => _host.Get<Keel.Application.Ledger.ITransactionService>().SaveAsync(new(null, checking.Id, new DateOnly(2026, 8, 9), -4_250, "Trader Joe's", null, null), CancellationToken.None));

        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.OpenAccount(checking.Id);
        await _host.Get<AccountsViewModel>().SettleAsync();
        var themes = _host.Get<ThemeService>();
        var workflow = _host.Get<Keel.Desktop.ViewModels.Import.ImportWorkflow>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        var ofx = ImportDialogTests.Ofx(("A1", "20260802", "-10.00", "OLD COFFEE"), ("A2", "20260803", "-21.00", "GAS"), ("A3", "20260810", "-42.50", "TRADER JOE'S #552"),
            ("A4", "20260805", "-300.00", "TRANSFER TO SAVINGS"), ("A5", "20260811", "-9.99", "SQ *NEW CAFE PORTLAND"), ("A6", "20260812", "1850.00", "PAYROLL ACME CORP"));

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            workflow.FilePicker = new FakeFilePicker(ImportDialogTests.File("export.csv", ImportDialogTests.AmbiguousCsv),
                new Keel.Desktop.ViewModels.Import.PickedImportFile("statement.ofx", null, System.Text.Encoding.UTF8.GetBytes(ofx)));

            var run = workflow.ImportAsync(checking.Id);
            var mapping = await ImportDialogTests.DialogAsync<Keel.Desktop.ViewModels.Import.CsvMappingViewModel>(shell);
            await mapping.Refreshing;
            Dispatcher.UIThread.RunJobs();
            Save(window, outputDir, $"CsvMappingDialog-{theme}.png");
            mapping.CancelCommand.Execute(null);
            await run;

            run = workflow.ImportAsync(checking.Id);
            var preview = await ImportDialogTests.DialogAsync<Keel.Desktop.ViewModels.Import.ImportPreviewViewModel>(shell);
            preview.Rows.Count.ShouldBe(6);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Save(window, outputDir, $"ImportPreviewDialog-{theme}.png");
            preview.CancelCommand.Execute(null);
            await run;
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();

        static void Save(Window window, string? outputDir, string name)
        {
            using var frame = window.CaptureRenderedFrame()!;
            frame.PixelSize.Width.ShouldBeGreaterThan(0);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                frame.Save(Path.Combine(outputDir, name));
            }
        }
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
