using ReactiveUI;
using System; // For Guid

namespace MyApp.ViewModels
{
    public class TransactionsViewModel : ReactiveObject, IRoutableViewModel
    {
        public string UrlPathSegment { get; } = Guid.NewGuid().ToString().Substring(0, 5);
        public IScreen HostScreen { get; }

        public TransactionsViewModel(IScreen screen)
        {
            HostScreen = screen;
        }
    }
} 