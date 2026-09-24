using Keel.Application.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Services;

/// <summary>View-model-first navigation: resolves the view model from DI and makes it current.</summary>
public sealed class NavigationService(IServiceProvider services) : INavigationService
{
    /// <inheritdoc />
    public object? Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler<NavigatedEventArgs>? Navigated;

    /// <inheritdoc />
    public void NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : class
    {
        var viewModel = services.GetRequiredService<TViewModel>();
        if (viewModel is INavigationTarget target)
        {
            target.OnNavigatedTo(parameter);
        }

        Current = viewModel;
        Navigated?.Invoke(this, new NavigatedEventArgs(viewModel, parameter));
    }
}
