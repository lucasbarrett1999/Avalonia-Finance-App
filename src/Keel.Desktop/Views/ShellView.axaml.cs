using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop.Views;

/// <summary>Shell layout. Keeps the sidebar column in sync with the view model (resizable, collapsible).</summary>
public partial class ShellView : UserControl
{
    private const double CollapsedWidth = 60;
    private ShellViewModel? _shell;

    /// <summary>Creates the view.</summary>
    public ShellView()
    {
        InitializeComponent();
        SidebarSplitter.DragCompleted += (_, _) =>
        {
            if (_shell is { IsSidebarCollapsed: false })
            {
                _shell.ResizeSidebar(BodyGrid.ColumnDefinitions[0].ActualWidth);
                ApplySidebarWidth();
            }
        };
        NotificationDismissArea.PointerPressed += (_, _) => _shell?.Notifications?.Close();
    }

    /// <summary>Moves keyboard focus to the global search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus(NavigationMethod.Tab);
        SearchBox.SelectAll();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _shell.SearchFocusRequested -= OnSearchFocusRequested;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            _shell.SearchFocusRequested += OnSearchFocusRequested;
        }

        BuildMenu();

        ApplySidebarWidth();
    }

    /// <summary>Fills the in-window menu bar (hidden on macOS, which shows the native menu instead).</summary>
    public void BuildMenu()
    {
        MainMenu.Items.Clear();
        MainMenu.IsVisible = !OperatingSystem.IsMacOS() && _shell?.Commands is not null;
        if (!MainMenu.IsVisible || _shell?.Commands is not { } commands)
        {
            return;
        }

        foreach (var node in commands.BuildMenu(_shell, macOS: false))
        {
            MainMenu.Items.Add(Services.MenuBuilder.ToMenuItem(node));
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e) => FocusSearch();

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsSidebarCollapsed) or nameof(ShellViewModel.SidebarWidth))
        {
            ApplySidebarWidth();
        }
    }

    private void ApplySidebarWidth()
    {
        if (_shell is null)
        {
            return;
        }

        var column = BodyGrid.ColumnDefinitions[0];
        column.Width = new GridLength(_shell.IsSidebarCollapsed ? CollapsedWidth : _shell.SidebarWidth);
        column.MinWidth = _shell.IsSidebarCollapsed ? CollapsedWidth : ShellViewModel.MinSidebarWidth;
        column.MaxWidth = _shell.IsSidebarCollapsed ? CollapsedWidth : ShellViewModel.MaxSidebarWidth;
    }
}
