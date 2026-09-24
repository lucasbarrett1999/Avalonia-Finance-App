namespace Keel.Application.Navigation;

/// <summary>
/// View-model-first navigation (PRD 7.3). The Shell hosts the current view model; a
/// ViewLocator maps FooViewModel to FooView by convention.
/// </summary>
public interface INavigationService
{
    /// <summary>The view model currently shown in the Shell's content area.</summary>
    object? Current { get; }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event EventHandler<NavigatedEventArgs>? Navigated;

    /// <summary>Resolves <typeparamref name="TViewModel"/> and shows it.</summary>
    /// <param name="parameter">Optional parameter passed to <see cref="INavigationTarget.OnNavigatedTo"/>.</param>
    void NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : class;
}

/// <summary>Implemented by view models that want the navigation parameter.</summary>
public interface INavigationTarget
{
    /// <summary>Called each time the view model becomes current.</summary>
    void OnNavigatedTo(object? parameter);
}

/// <summary>Arguments of <see cref="INavigationService.Navigated"/>.</summary>
/// <param name="ViewModel">The new current view model.</param>
/// <param name="Parameter">The navigation parameter.</param>
public sealed record NavigatedEventArgs(object ViewModel, object? Parameter);
