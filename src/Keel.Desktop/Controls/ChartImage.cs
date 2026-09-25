using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LiveChartsCore.SkiaSharpView.Avalonia;

namespace Keel.Desktop.Controls;

/// <summary>
/// PNG export of report charts (PRD 9.8, ADR 0095): renders a chart control through Avalonia's
/// <see cref="RenderTargetBitmap"/> at twice its size onto the chart surface colour of the current theme.
/// </summary>
public static class ChartImage
{
    /// <summary>Scale of exported images (2x, crisp on high-DPI screens and in documents).</summary>
    public const double Scale = 2;

    /// <summary>The first visible, laid-out LiveCharts chart inside <paramref name="root"/>, or null.</summary>
    public static Control? FindChart(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c is CartesianChart or PieChart && c.IsEffectivelyVisible && c.Bounds.Width >= 1 && c.Bounds.Height >= 1);
    }

    /// <summary>Renders <paramref name="chart"/> at <see cref="Scale"/> and writes it to <paramref name="stream"/> as PNG; returns the pixel size.</summary>
    public static PixelSize Save(Control chart, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(stream);
        var size = chart.Bounds.Size;
        var pixels = new PixelSize((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale));
        var dpi = new Vector(96 * Scale, 96 * Scale);
        using var content = new RenderTargetBitmap(pixels, dpi);
        content.Render(chart);

        // Charts draw on a transparent background; give the image the chart surface of the theme.
        using var image = new RenderTargetBitmap(pixels, dpi);
        using (var context = image.CreateDrawingContext())
        {
            var bounds = new Rect(size);
            context.FillRectangle(new SolidColorBrush(ChartPalette.Resolve(chart, "Keel.Chart.Surface")), bounds);
            // The source rectangle of a bitmap is in its pixels.
            context.DrawImage(content, new Rect(0, 0, pixels.Width, pixels.Height), bounds);
        }

        image.Save(stream);
        return pixels;
    }

    /// <summary>Renders <paramref name="chart"/> to a PNG file.</summary>
    public static PixelSize Save(Control chart, string path)
    {
        using var file = File.Create(path);
        return Save(chart, file);
    }
}
