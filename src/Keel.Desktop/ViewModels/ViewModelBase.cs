using CommunityToolkit.Mvvm.ComponentModel;

namespace Keel.Desktop.ViewModels;

/// <summary>Base class for view models that the <see cref="ViewLocator"/> maps to views.</summary>
public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>A screen shown in the shell's content area, with its header and empty state.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Page title.</summary>
    public abstract string Title { get; }

    /// <summary>One-line description under the title.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Empty-state heading.</summary>
    public abstract string EmptyHeading { get; }

    /// <summary>Empty-state message explaining the next step.</summary>
    public abstract string EmptyMessage { get; }
}
