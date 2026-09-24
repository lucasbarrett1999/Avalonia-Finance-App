using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Keel.Desktop.ViewModels;

namespace Keel.Desktop;

/// <summary>
/// View-model-first view resolution (PRD 7.3): <c>Keel.Desktop.ViewModels.FooViewModel</c> is
/// shown with <c>Keel.Desktop.Views.FooView</c>. The map is built once by convention.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly IReadOnlyDictionary<Type, Type> Map = BuildMap(typeof(ViewLocator).Assembly);

    /// <summary>The view type for a view-model type, or null when none follows the convention.</summary>
    public static Type? ViewTypeFor(Type viewModelType) =>
        Map.TryGetValue(viewModelType, out var viewType) ? viewType : null;

    /// <inheritdoc />
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var viewType = ViewTypeFor(param.GetType());
        return viewType is null
            ? new TextBlock { Text = "No view for " + param.GetType().Name }
            : (Control)Activator.CreateInstance(viewType)!;
    }

    /// <inheritdoc />
    public bool Match(object? data) => data is ViewModelBase;

    private static Dictionary<Type, Type> BuildMap(Assembly assembly)
    {
        var types = assembly.GetTypes();
        var views = types
            .Where(t => !t.IsAbstract && typeof(Control).IsAssignableFrom(t) && t.FullName is not null)
            .ToDictionary(t => t.FullName!, StringComparer.Ordinal);

        var map = new Dictionary<Type, Type>();
        foreach (var vm in types.Where(t => !t.IsAbstract && typeof(ViewModelBase).IsAssignableFrom(t)))
        {
            var viewName = vm.FullName!
                .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
                .Replace("ViewModel", "View", StringComparison.Ordinal);
            if (views.TryGetValue(viewName, out var view))
            {
                map[vm] = view;
            }
        }

        return map;
    }
}
