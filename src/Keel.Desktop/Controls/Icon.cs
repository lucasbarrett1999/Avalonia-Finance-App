using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Keel.Desktop.Controls;

/// <summary>
/// A vector line icon: <see cref="Data"/> is drawn on a 24x24 grid with a round 2-px stroke in
/// the inherited <see cref="TemplatedControl.Foreground"/>, so icons follow the theme and scale
/// crisply at any DPI.
/// </summary>
public class Icon : TemplatedControl
{
    /// <summary>Defines <see cref="Data"/>.</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>Icon geometry on a 24x24 grid.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
