using ReactiveUI;
using System; // For Guid

namespace MyApp.ViewModels;

public class DashboardViewModel : ReactiveObject, IRoutableViewModel
{
    // Reference to IScreen that owns the routable view model.
    public IScreen HostScreen { get; }

    // Unique identifier for the routable view model.
    public string UrlPathSegment { get; } = Guid.NewGuid().ToString().Substring(0, 5);

    public DashboardViewModel(IScreen screen)
    {
        HostScreen = screen;
    }
} 