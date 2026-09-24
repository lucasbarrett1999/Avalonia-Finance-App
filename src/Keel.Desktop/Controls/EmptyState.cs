using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Keel.Desktop.Controls;

/// <summary>
/// The designed empty state every screen shows before it has data (PRD 9.11): an icon, a
/// heading, a message explaining the next step, and optional extra content.
/// </summary>
public class EmptyState : ContentControl
{
    /// <summary>Defines <see cref="Icon"/>.</summary>
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));

    /// <summary>Defines <see cref="Heading"/>.</summary>
    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Heading));

    /// <summary>Defines <see cref="Message"/>.</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Message));

    /// <summary>Icon geometry.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Heading text.</summary>
    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    /// <summary>Explanation of what will appear and how to get there.</summary>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
