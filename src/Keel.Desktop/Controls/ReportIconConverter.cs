using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Keel.Desktop.Controls;

/// <summary>Looks up an icon geometry by resource key (report list icons come from view models as keys).</summary>
public sealed class ReportIconConverter : IValueConverter
{
    /// <summary>The shared instance.</summary>
    public static ReportIconConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var resource) && resource is Geometry geometry
            ? geometry
            : null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
