using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.ViewModels.Sync;
using Keel.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Services;

/// <summary>DI registration for the desktop head.</summary>
public static class DependencyInjection
{
    /// <summary>Registers UI services, the shell, and one view model per screen.</summary>
    public static IServiceCollection AddKeelDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<IMessageBus, MessengerBus>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<AppSession>();
        services.AddSingleton<BudgetFileStartup>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton(_ => PlatformShortcuts.FromCurrentPlatform());
        services.AddSingleton<DialogService>();
        services.AddSingleton<StatusService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<BudgetViewModel>();
        services.AddSingleton<ReviewViewModel>();
        services.AddSingleton<BillsViewModel>();
        services.AddSingleton<GoalsViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();

        services.AddTransient<ShellWindow>();
        services.AddSingleton<IImportFilePicker, StorageImportFilePicker>();
        services.AddSingleton<ImportWorkflow>();
        services.AddSingleton<IBrowserLauncher, AvaloniaBrowserLauncher>();
        services.AddSingleton<SyncCoordinator>();
        services.AddSingleton<ConnectionsSettingsViewModel>();
        return services;
    }
}
