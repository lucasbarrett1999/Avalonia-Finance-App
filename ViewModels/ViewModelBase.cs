using ReactiveUI;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyApp.ViewModels;

// This base class is used by all ViewModels and provides both ReactiveUI and
// standard .NET property change notification support.
public class ViewModelBase : ReactiveObject
{
    // These methods will be called by the generated code from [ObservableProperty]
    // attributes in derived classes
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        this.RaisePropertyChanged(propertyName);
    }
    
    protected void OnPropertyChanging([CallerMemberName] string propertyName = null)
    {
        this.RaisePropertyChanging(propertyName);
    }
}