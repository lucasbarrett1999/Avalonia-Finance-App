using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Keel.Desktop.Resources;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop.Views;

/// <summary>View for <see cref="ReportsViewModel"/>. Supplies the save-file picker for CSV export.</summary>
public partial class ReportsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ReportsView()
    {
        InitializeComponent();

        // Registered in ShortcutRegistry: digits choose a report, Ctrl/Cmd+E exports CSV, Ctrl/Cmd+Shift+E exports PNG.
        Services.PageKeys.Attach(this, e =>
        {
            if (DataContext is not ReportsViewModel vm)
            {
                return false;
            }

            if (Services.PageKeys.Digit(e) is { } digit && digit <= vm.Reports.Count)
            {
                vm.SelectedReport = vm.Reports[digit - 1];
                return true;
            }

            var command = Services.PlatformShortcuts.FromCurrentPlatform().CommandModifiers;
            if (e.Key == Avalonia.Input.Key.E && e.KeyModifiers == command)
            {
                vm.ExportCsvCommand.Execute(null);
                return true;
            }

            if (e.Key == Avalonia.Input.Key.E && e.KeyModifiers == (command | Avalonia.Input.KeyModifiers.Shift))
            {
                vm.ExportPngCommand.Execute(null);
                return true;
            }

            return false;
        });
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ReportsViewModel vm && vm.SaveFile is null)
        {
            vm.SaveFile = SaveAsync;
        }

        if (DataContext is ReportsViewModel png && png.RenderChartPng is null)
        {
            png.RenderChartPng = RenderChartPng;
        }
    }

    // PNG export (PRD 9.8, ADR 0095): the selected report's chart at 2x.
    private bool RenderChartPng(string path)
    {
        if (Controls.ChartImage.FindChart(ReportHost) is not { } chart)
        {
            return false;
        }

        Controls.ChartImage.Save(chart, path);
        return true;
    }

    private async Task<string?> SaveAsync(string suggestedName, string content)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Reports_ExportCsv,
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType(Strings.Reports_CsvFileType) { Patterns = ["*.csv"], MimeTypes = ["text/csv"] }],
        });
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(content);
        return file.Name;
    }
}
