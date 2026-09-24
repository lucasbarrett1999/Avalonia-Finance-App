using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Keel.Desktop.Converters;

/// <summary>Converters that look up application resources by key.</summary>
public static class ResourceConverters
{
    /// <summary>Converts a resource key (e.g. "Icon.Home") to the <see cref="Geometry"/> it names.</summary>
    public static IValueConverter Geometry { get; } = new FuncValueConverter<string?, Geometry?>(key =>
        key is not null
        && Avalonia.Application.Current is { } app
        && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as Geometry
            : null);
}
