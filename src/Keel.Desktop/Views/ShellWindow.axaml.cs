using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop.Views;

/// <summary>The main window. Hosts the shell and persists its placement per display configuration.</summary>
public partial class ShellWindow : Window
{
    private readonly WindowPlacementService? _placement;
    private Size _normalSize;
    private PixelPoint? _normalPosition;

    /// <summary>Designer constructor.</summary>
    public ShellWindow()
    {
        InitializeComponent();
    }

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    public ShellWindow(ShellViewModel viewModel, WindowPlacementService placement)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        _placement = placement;

        AddKeyBindings(viewModel);
        RestorePlacement(viewModel);
        _normalSize = new Size(Width, Height);

#if DEBUG
        this.AttachDevTools();
#endif
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (WindowState == WindowState.Normal && (change.Property == ClientSizeProperty || change.Property == WindowStateProperty))
        {
            _normalSize = ClientSize;
        }
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionChanged += (_, args) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalPosition = args.Point;
            }
        };
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        SavePlacement();
    }

    /// <summary>Writes the current placement to settings.json.</summary>
    public void SavePlacement()
    {
        if (_placement is null || DataContext is not ShellViewModel shell)
        {
            return;
        }

        var position = _normalPosition ?? (WindowState == WindowState.Normal ? Position : (PixelPoint?)null);
        _placement.Save(this, new WindowPlacement(
            _normalSize.Width,
            _normalSize.Height,
            position?.X,
            position?.Y,
            WindowState == WindowState.Maximized,
            shell.SidebarWidth,
            shell.IsSidebarCollapsed));
    }

    private void RestorePlacement(ShellViewModel shell)
    {
        if (_placement?.Find(this) is not { } saved)
        {
            return;
        }

        WindowPlacementService.Apply(this, saved);
        shell.ResizeSidebar(saved.SidebarWidth);
        shell.IsSidebarCollapsed = saved.IsSidebarCollapsed;
    }

    private void AddKeyBindings(ShellViewModel shell)
    {
        var shortcuts = shell.Shortcuts;
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Search, Command = new RelayCommand(() => Shell.FocusSearch()) });
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Undo, Command = shell.UndoCommand });
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Redo, Command = shell.RedoCommand });
        var shiftRedo = new KeyGesture(Key.Z, shortcuts.CommandModifiers | KeyModifiers.Shift);
        if (!shiftRedo.Equals(shortcuts.Redo))
        {
            // Ctrl/Cmd+Shift+Z redoes everywhere, in addition to the platform's own redo gesture.
            KeyBindings.Add(new KeyBinding { Gesture = shiftRedo, Command = shell.RedoCommand });
        }
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.ToggleSidebar, Command = shell.ToggleSidebarCommand });
    }
}
