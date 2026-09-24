using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Keel.Desktop.ViewModels.Rules;

namespace Keel.Desktop.Views.Rules;

/// <summary>
/// The rules list, shown in Settings → Rules and on the Rules page. Code-behind loads the list when
/// it first appears and implements drag-to-reorder from a row's handle (the up/down buttons and the
/// view model's <see cref="RulesViewModel.MoveToAsync"/> do the same without a mouse).
/// </summary>
public partial class RulesPanel : UserControl
{
    private const string DragFormat = "keel-rule-row";

    /// <summary>Creates the view.</summary>
    public RulesPanel()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is RulesViewModel vm)
        {
            _ = vm.EnsureLoadedAsync();
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RulesViewModel vm && this.IsAttachedToVisualTree())
        {
            _ = vm.EnsureLoadedAsync();
        }
    }

    private static Border? RowOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("ruleRow"));

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var handle = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("dragHandle"));
        if (handle?.Tag is not RuleRowViewModel row || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

#pragma warning disable CS0618 // DataObject is the drag payload API of Avalonia 11.3's DragDrop.DoDragDrop.
        var data = new DataObject();
        data.Set(DragFormat, row);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var dragging = e.Data.Contains(DragFormat);
#pragma warning restore CS0618
        e.DragEffects = dragging ? DragDropEffects.Move : DragDropEffects.None;
        foreach (var border in RuleRows.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ruleRow")))
        {
            border.Classes.Set("dropTarget", dragging && ReferenceEquals(border, RowOf(e.Source)));
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ClearDropTargets();

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropTargets();
#pragma warning disable CS0618
        var source = e.Data.Get(DragFormat) as RuleRowViewModel;
#pragma warning restore CS0618
        if (source is null || RowOf(e.Source)?.Tag is not RuleRowViewModel target || DataContext is not RulesViewModel vm)
        {
            return;
        }

        await vm.MoveToAsync(source, vm.Rules.IndexOf(target));
    }

    private void ClearDropTargets()
    {
        foreach (var border in RuleRows.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ruleRow")))
        {
            border.Classes.Set("dropTarget", false);
        }
    }
}
