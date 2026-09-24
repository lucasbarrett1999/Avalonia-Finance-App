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
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ReportsViewModel vm && vm.SaveFile is null)
        {
            vm.SaveFile = SaveAsync;
        }
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
