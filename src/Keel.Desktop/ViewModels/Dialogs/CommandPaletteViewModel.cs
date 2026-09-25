using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>
/// The command palette (Ctrl/Cmd+K, PRD 9.1, F-SET-5): every action and navigation target, fuzzy
/// filtered as the user types. ↑/↓ move, Enter runs, Esc closes. The command runs after the palette closes.
/// </summary>
public sealed partial class CommandPaletteViewModel : DialogViewModel
{
    /// <summary>Most results listed at once.</summary>
    public const int MaxResults = 60;

    private readonly IReadOnlyList<AppCommand> _commands;

    /// <summary>Creates the palette over <paramref name="commands"/>.</summary>
    public CommandPaletteViewModel(IReadOnlyList<AppCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
        Results = commands.Take(MaxResults).ToList();
        Selected = Results.FirstOrDefault(c => c.IsEnabled) ?? Results.FirstOrDefault();
    }

    /// <inheritdoc />
    public override string Title => Strings.Palette_Title;

    /// <inheritdoc />
    public override double PreferredMaxWidth => 640;

    /// <summary>All commands.</summary>
    public IReadOnlyList<AppCommand> Commands => _commands;

    /// <summary>The search text.</summary>
    [ObservableProperty]
    public partial string? Query { get; set; }

    /// <summary>Matching commands, best first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    public partial IReadOnlyList<AppCommand> Results { get; private set; }

    /// <summary>The highlighted command.</summary>
    [ObservableProperty]
    public partial AppCommand? Selected { get; set; }

    /// <summary>Nothing matches the query.</summary>
    public bool HasNoResults => Results.Count == 0;

    /// <summary>The command chosen when the palette closed with Enter or a click.</summary>
    public AppCommand? Chosen { get; private set; }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows (wrapping).</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : IndexOf(Selected);
        index = ((index + delta) % Results.Count + Results.Count) % Results.Count;
        Selected = Results[index];
    }

    /// <inheritdoc />
    protected override Task<bool> ConfirmCoreAsync()
    {
        if (Selected is { IsEnabled: false })
        {
            // Unavailable actions are listed (so they can be found) but never run (ADR 0103).
            Error = Strings.Palette_UnavailableError;
            return Task.FromResult(false);
        }

        Chosen = Selected;
        return Task.FromResult(Chosen is not null);
    }

    partial void OnQueryChanged(string? value)
    {
        Results = FuzzyMatch.Filter(_commands, value, c => c.Title, c => c.Section).Take(MaxResults).ToList();
        Selected = Results.FirstOrDefault(c => c.IsEnabled) ?? Results.FirstOrDefault();
        Error = null;
    }

    private int IndexOf(AppCommand command)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            if (ReferenceEquals(Results[i], command))
            {
                return i;
            }
        }

        return -1;
    }
}
