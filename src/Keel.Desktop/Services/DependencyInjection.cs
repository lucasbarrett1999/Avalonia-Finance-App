using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.ViewModels.Rules;
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

        // One messenger per session (M8, ADR 0080): screens of a closed budget file never receive the next file's messages.
        services.AddSingleton<IMessenger>(_ => new WeakReferenceMessenger());
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
        services.AddSingleton<RuleEditorFlow>();
        services.AddSingleton<RulesViewModel>();
        services.AddSingleton<PayeesViewModel>();
        services.AddSingleton<Keel.Desktop.ViewModels.Bills.ScheduleEditorLauncher>();
        services.AddTransient<Keel.Desktop.ViewModels.Bills.ScheduledGhostsViewModel>();
        services.AddSingleton<Keel.Desktop.ViewModels.Alerts.NotificationCenterViewModel>();
        services.AddSingleton<RecurringJobs>();
        services.AddSingleton<ShortcutRegistry>();
        services.AddSingleton<AppCommands>();
        services.AddSingleton<AppearanceService>();
        services.AddSingleton<LocaleService>();
        services.AddSingleton<IFileDialogs, StorageFileDialogs>();
        services.AddSingleton<Func<IUpdateSource>>(_ => () => new VelopackUpdateSource());
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MaintenanceJobs>();
        services.AddSingleton<Keel.Desktop.ViewModels.Settings.DataFileSettingsViewModel>();
        services.AddSingleton<Keel.Desktop.ViewModels.Settings.AppearanceSettingsViewModel>();
        services.AddSingleton<Keel.Desktop.ViewModels.Settings.BillsSettingsViewModel>();
        services.AddSingleton<Keel.Desktop.ViewModels.Settings.UpdatesSettingsViewModel>();
        services.AddTransient<Keel.Desktop.ViewModels.FirstRun.FirstRunViewModel>();
        services.AddSingleton<IPortabilityDialogs, StoragePortabilityDialogs>();
        services.AddSingleton<Keel.Desktop.ViewModels.Portability.MigrationWorkflow>();
        services.AddSingleton<Keel.Desktop.ViewModels.Portability.PortabilitySettingsViewModel>();
        services.AddSingleton<Keel.Desktop.ViewModels.Goals.DebtPayoffViewModel>();
        return services;
    }
}
