using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Keel.Desktop.Services;

/// <summary>Turns <see cref="MenuNode"/> trees into Avalonia menus: in-window <see cref="MenuItem"/>s and macOS <see cref="NativeMenu"/>s.</summary>
public static class MenuBuilder
{
    /// <summary>An in-window menu item (with access keys and the gesture text).</summary>
    public static Control ToMenuItem(MenuNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsSeparator)
        {
            return new Separator();
        }

        var item = new MenuItem { Header = node.Header };
        Avalonia.Automation.AutomationProperties.SetName(item, node.Header.Replace("_", string.Empty, StringComparison.Ordinal));
        if (node.Children is { } children)
        {
            foreach (var child in children)
            {
                item.Items.Add(ToMenuItem(child));
            }
        }
        else if (node.Command is { } command)
        {
            item.Command = new RelayCommand(command.Execute);
            item.InputGesture = command.Gesture;
        }

        return item;
    }

    /// <summary>A native menu (macOS menu bar).</summary>
    public static NativeMenu ToNativeMenu(IEnumerable<MenuNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var menu = new NativeMenu();
        foreach (var node in nodes)
        {
            menu.Items.Add(ToNativeItem(node));
        }

        return menu;
    }

    private static NativeMenuItemBase ToNativeItem(MenuNode node)
    {
        if (node.IsSeparator)
        {
            return new NativeMenuItemSeparator();
        }

        var item = new NativeMenuItem(node.Header.Replace("_", string.Empty, StringComparison.Ordinal));
        if (node.Children is { } children)
        {
            item.Menu = ToNativeMenu(children);
        }
        else if (node.Command is { } command)
        {
            item.Command = new RelayCommand(command.Execute);
            item.Gesture = command.Gesture;
        }

        return item;
    }
}
