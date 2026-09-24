using Keel.Application.Categories;
using Keel.Desktop.Resources;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>
/// A starter category set (F-BUD-8). The content lives in Strings.resx as
/// "Group: Category, Category; Group: ..." so it is localizable.
/// </summary>
/// <param name="Name">Template name.</param>
/// <param name="Groups">Groups and categories.</param>
public sealed record BudgetTemplate(string Name, IReadOnlyList<CategoryTemplateGroup> Groups)
{
    /// <summary>Simple, Detailed, Student, Family.</summary>
    public static IReadOnlyList<BudgetTemplate> All { get; } =
    [
        Parse(Strings.Template_Simple_Name, Strings.Template_Simple),
        Parse(Strings.Template_Detailed_Name, Strings.Template_Detailed),
        Parse(Strings.Template_Student_Name, Strings.Template_Student),
        Parse(Strings.Template_Family_Name, Strings.Template_Family),
    ];

    /// <summary>Preview for the tooltip: one line per group.</summary>
    public string Preview => string.Join(Environment.NewLine, Groups.Select(g => g.Name + ": " + string.Join(", ", g.Categories)));

    /// <summary>Accessible name of the template button.</summary>
    public string AutomationName => Name + ". " + Preview;

    /// <summary>Parses "Group: A, B; Group: C".</summary>
    public static BudgetTemplate Parse(string name, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var groups = content.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => new CategoryTemplateGroup(p[0], p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .ToList();
        return new BudgetTemplate(name, groups);
    }
}
