using System.Globalization;
using Avalonia.Data.Converters;
using Keel.Desktop.ViewModels.Sync;

namespace Keel.Desktop.Views.Sync;

/// <summary>True when a <see cref="HealthKind"/> equals the converter parameter (for health-dot style classes).</summary>
public sealed class HealthKindConverter : IValueConverter
{
    /// <summary>The shared instance.</summary>
    public static HealthKindConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HealthKind kind && parameter is string name && Enum.TryParse<HealthKind>(name, out var expected) && kind == expected;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
