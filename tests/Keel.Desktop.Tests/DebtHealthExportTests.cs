using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>M9b headless flows: PNG export of report charts, the budget health report and the debt payoff planner.</summary>
public sealed class DebtHealthExportTests : IDisposable
{
    private readonly FakeFileDialogs _files = new();
    private readonly TestHost _host;
    private readonly string _out = Path.Combine(Path.GetTempPath(), "keel-png-tests", Guid.NewGuid().ToString("N"));

    public DebtHealthExportTests() => _host = TestHost.Create(services => services.AddSingleton<IFileDialogs>(_files));

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_out, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private Task<LedgerFixture> FixtureAsync(int count = 3_000) =>
        Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(count, Seed: 7, EndDate: Today), Ct));

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        return (window, shell);
    }

    private static async Task SettleAsync(Func<Task> loading)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await loading();
            await Task.Delay(15);
        }

        // LiveCharts redraws through its own throttled loop.
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(40);
        }
    }

    [AvaloniaFact]
    public async Task Every_report_exports_its_chart_as_a_2x_png()
    {
        await FixtureAsync();
        var (window, shell) = await ShowAsync();
        shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
        var reports = _host.Get<ReportsViewModel>();
        await SettleAsync(() => reports.Loading);
        Directory.CreateDirectory(_out);
        var shots = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        foreach (var report in reports.Reports)
        {
            reports.SelectedReport = report;
            await SettleAsync(() => reports.Loading);
            report.HasData.ShouldBeTrue(report.Kind.ToString());
            var path = Path.Combine(_out, report.PngFileName);
            _files.SavePngs.Enqueue(path);
            var chart = ChartImage.FindChart(window.Named<ContentControl>("ReportHost"));
            chart.ShouldNotBeNull(report.Kind.ToString());

            window.Named<Button>("ExportPngButton").Command!.Execute(null);
            await UiTestHelpers.WaitUntilAsync(() => File.Exists(path), "png written for " + report.Kind);
            await UiTestHelpers.WaitUntilAsync(() => _host.Get<StatusService>().Message.Contains(report.PngFileName, StringComparison.Ordinal), "status");

            var bytes = await File.ReadAllBytesAsync(path);
            bytes.Take(8).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG signature");
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            width.ShouldBe((int)Math.Ceiling(chart.Bounds.Width * ChartImage.Scale), report.Kind.ToString());
            height.ShouldBe((int)Math.Ceiling(chart.Bounds.Height * ChartImage.Scale), report.Kind.ToString());
            bytes.Length.ShouldBeGreaterThan(2_000, "the image has content");
            if (!string.IsNullOrEmpty(shots))
            {
                Directory.CreateDirectory(shots);
                File.Copy(path, Path.Combine(shots, "Export-" + report.PngFileName), overwrite: true);
            }
        }

        _files.PngSuggestions.ShouldBe(reports.Reports.Select(r => r.PngFileName));
        _files.PngSuggestions.ShouldContain("budget-health.png");

        // Cancelling the picker writes nothing; the keyboard shortcut runs the same command.
        var before = Directory.GetFiles(_out).Length;
        window.Press(Avalonia.Input.PhysicalKey.E, _host.Get<PlatformShortcuts>().Command() | Avalonia.Input.RawInputModifiers.Shift);
        await UiTestHelpers.WaitUntilAsync(() => _files.PngSuggestions.Count == reports.Reports.Count + 1, "shortcut asked for a file");
        Directory.GetFiles(_out).Length.ShouldBe(before);
        window.Close();
    }
}
