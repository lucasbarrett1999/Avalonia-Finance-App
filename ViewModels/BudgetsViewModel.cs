using ReactiveUI;
using System; // For Guid

namespace MyApp.ViewModels
{
    public class BudgetsViewModel : ReactiveObject, IRoutableViewModel
    {
        public string UrlPathSegment { get; } = Guid.NewGuid().ToString().Substring(0, 5);
        public IScreen HostScreen { get; }

        public BudgetsViewModel(IScreen screen)
        {
            HostScreen = screen;
        }
    }
} 