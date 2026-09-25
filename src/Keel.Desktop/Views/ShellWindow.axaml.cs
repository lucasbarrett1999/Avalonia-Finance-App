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
    private WindowPlacementService? _placement;
    private AppearanceService? _appearance;
    private Size _normalSize;
    private PixelPoint? _normalPosition;

    /// <summary>Designer constructor.</summary>
    public ShellWindow()
    {
        InitializeComponent();
    }

    // Stats page (PRD 4): the cold start ends at the first frame drawn after the shell's first loads.
    private async Task ReportColdStartAsync()
    {
        if (DataContext is not ShellViewModel shell || shell.StatsInstrumentation is not { IsColdStartPending: true } stats)
        {
            return;
        }

        await shell.WhenLoadedAsync();
        RequestAnimationFrame(_ => stats.MarkInteractiveAsync());
    }

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    public ShellWindow(ShellViewModel viewModel, WindowPlacementService placement, BudgetSessions? sessions = null, AppearanceService? appearance = null)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        _placement = placement;
        _appearance = appearance;

        AddKeyBindings(viewModel);
        RestorePlacement(viewModel);
        _normalSize = new Size(Width, Height);
        appearance?.ApplySaved(this);
        ApplyNativeMenu(viewModel);
        sessions?.AttachWindow(this);

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
        _ = ReportColdStartAsync();
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

    /// <summary>
    /// Moves the window to a new budget-file session (ADR 0080): its shell, key bindings and menus. The
    /// sidebar state and window placement carry over.
    /// </summary>
    public void Attach(ShellViewModel shell, WindowPlacementService placement)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (DataContext is ShellViewModel previous)
        {
            shell.ResizeSidebar(previous.SidebarWidth);
            shell.IsSidebarCollapsed = previous.IsSidebarCollapsed;
        }

        _placement = placement;
        DataContext = shell;
        KeyBindings.Clear();
        AddKeyBindings(shell);
        ApplyNativeMenu(shell);
        _appearance?.ApplyWindowClasses(this);
    }

    private void ApplyNativeMenu(ShellViewModel shell)
    {
        if (!OperatingSystem.IsMacOS() || shell.Commands is not { } commands)
        {
            return;
        }

        NativeMenu.SetMenu(this, MenuBuilder.ToNativeMenu(commands.BuildMenu(shell, macOS: true)));
        if (Avalonia.Application.Current is { } app)
        {
            NativeMenu.SetMenu(app, MenuBuilder.ToNativeMenu(commands.BuildAppMenu(shell)));
        }
    }

    private void AddKeyBindings(ShellViewModel shell)
    {
        var shortcuts = shell.Shortcuts;
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.CommandPalette, Command = shell.OpenCommandPaletteCommand });
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Settings, Command = new RelayCommand(() => shell.NavigateToSettings(null)) });
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.SyncAll, Command = shell.SyncAllCommand });
        if (shell.Commands is { } commands)
        {
            KeyBindings.Add(new KeyBinding { Gesture = shortcuts.OpenFile, Command = new RelayCommand(() => Run(commands, shell, "open-file")) });
            if (shortcuts.Quit is { } quit)
            {
                KeyBindings.Add(new KeyBinding { Gesture = quit, Command = new RelayCommand(Close) });
            }
        }

        var pages = shell.PrimaryItems.Concat(shell.AccountItems).ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            KeyBindings.Add(new KeyBinding { Gesture = shortcuts.GoTo(i), Command = pages[i].NavigateCommand });
        }

        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Search, Command = new RelayCommand(() => Shell.FocusSearch()) });
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Undo, Command = shell.UndoCommand });
        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.Redo, Command = shell.RedoCommand });
        if (!shortcuts.RedoAlternate.Equals(shortcuts.Redo))
        {
            // Ctrl/Cmd+Shift+Z redoes everywhere, in addition to the platform's own redo gesture.
            KeyBindings.Add(new KeyBinding { Gesture = shortcuts.RedoAlternate, Command = shell.RedoCommand });
        }

        KeyBindings.Add(new KeyBinding { Gesture = shortcuts.ToggleSidebar, Command = shell.ToggleSidebarCommand });
    }

    private static void Run(AppCommands commands, ShellViewModel shell, string id)
    {
        if (shell.FirstRun is null)
        {
            commands.Build(shell).FirstOrDefault(c => c.Id == id)?.Execute();
        }
    }
}
