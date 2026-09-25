using System.Windows.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Home;

/// <summary>A step of the Home "Get started" checklist.</summary>
/// <param name="Title">What to do.</param>
/// <param name="Hint">Why, in one line.</param>
/// <param name="IsDone">Done (a check mark and the word "Done", never colour alone).</param>
/// <param name="Command">Takes the user there.</param>
/// <param name="ActionText">Button text.</param>
public sealed record SetupStepViewModel(string Title, string Hint, bool IsDone, ICommand? Command, string ActionText)
{
    /// <summary>Show the button (open steps with an action).</summary>
    public bool ShowAction => !IsDone && Command is not null;

    /// <summary>"Done" or "To do".</summary>
    public string StateText => IsDone ? Strings.Setup_Done : Strings.Setup_ToDo;

    /// <summary>Screen-reader text.</summary>
    public string AutomationName => LedgerText.Format("{0}: {1}", Title, StateText);
}
