using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Keel.Desktop.ViewModels;

/// <summary>A sidebar entry.</summary>
public sealed partial class NavigationItemViewModel : ObservableObject
{
    private readonly Action _navigate;

    /// <summary>Creates an entry that calls <paramref name="navigate"/> when chosen.</summary>
    public NavigationItemViewModel(string title, string iconKey, Type pageType, Action navigate)
    {
        Title = title;
        IconKey = iconKey;
        PageType = pageType;
        _navigate = navigate;
    }

    /// <summary>Label.</summary>
    public string Title { get; }

    /// <summary>Resource key of the icon geometry.</summary>
    public string IconKey { get; }

    /// <summary>View-model type the entry navigates to.</summary>
    public Type PageType { get; }

    /// <summary>Whether this entry's page is current.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Count shown as a badge (e.g. unapproved transactions); hidden at 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    /// <summary>Whether the badge is visible.</summary>
    public bool HasBadge => Badge > 0;

    [RelayCommand]
    private void Navigate() => _navigate();
}
