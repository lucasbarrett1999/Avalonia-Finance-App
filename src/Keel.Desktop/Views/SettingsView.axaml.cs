using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop.Views;

/// <summary>View for <see cref="ViewModels.SettingsViewModel"/>. Scrolls to a requested section ("{Name}Section").</summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.SectionRequested -= OnSectionRequested;
        }

        _vm = DataContext as SettingsViewModel;
        if (_vm is not null)
        {
            _vm.SectionRequested += OnSectionRequested;
            OnSectionRequested(_vm, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null)
        {
            OnSectionRequested(_vm, EventArgs.Empty);
        }
    }

    private void OnSectionRequested(object? sender, EventArgs e)
    {
        if (_vm?.RequestedSection is not { Length: > 0 } section)
        {
            return;
        }

        // After layout, so the section has its final position.
        Dispatcher.UIThread.Post(
            () =>
            {
                var target = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == section + "Section");
                if (target is not null && SettingsScroll.Content is Visual content && target.TranslatePoint(new Avalonia.Point(0, 0), content) is { } point)
                {
                    SettingsScroll.Offset = new Avalonia.Vector(0, Math.Max(0, point.Y - 8));
                }
            },
            DispatcherPriority.Background);
    }
}
