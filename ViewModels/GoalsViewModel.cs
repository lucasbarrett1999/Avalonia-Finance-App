using ReactiveUI;
using System; // For Guid

namespace MyApp.ViewModels
{
    public class GoalsViewModel : ReactiveObject, IRoutableViewModel
    {
        public string UrlPathSegment { get; } = Guid.NewGuid().ToString().Substring(0, 5);
        public IScreen HostScreen { get; }

        public GoalsViewModel(IScreen screen)
        {
            HostScreen = screen;
        }
    }
} 