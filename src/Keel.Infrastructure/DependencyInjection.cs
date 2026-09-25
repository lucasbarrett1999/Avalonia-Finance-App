using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Categorization;
using Keel.Application.Files;
using Keel.Application.Goals;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Reports;
using Keel.Application.Rules;
using Keel.Application.Settings;
using Keel.Application.Undo;
using Keel.Infrastructure.Budgeting;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Goals;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Reports;
using Keel.Infrastructure.Rules;
using Keel.Infrastructure.Settings;
using Keel.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keel.Infrastructure;

/// <summary>DI registration for infrastructure services.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the data directory, settings.json store, database factory, budget-file service,
    /// and the ledger services (accounts, transactions, payees, categories, snapshots, register,
    /// undo). The host must also register an <c>IMessageBus</c>. Called from the desktop app's
    /// composition root only.
    /// </summary>
    public static IServiceCollection AddKeelInfrastructure(this IServiceCollection services, IDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        services.AddSingleton(dataDirectory);
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<KeelDbContextFactory>();
        services.AddSingleton<IDbContextFactory<KeelDbContext>>(sp => sp.GetRequiredService<KeelDbContextFactory>());
        services.AddSingleton<IBudgetFileService, BudgetFileService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<UndoHistory>();
        services.AddSingleton<LedgerWriter>();
        services.AddSingleton<IUndoService, UndoService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<ITransactionService, TransactionService>();
        services.AddSingleton<IPayeeService, PayeeService>();
        services.AddSingleton<ICategoryService, CategoryService>();
        services.AddSingleton<IBalanceSnapshotService, BalanceSnapshotService>();
        services.AddSingleton<IRegisterQuery, RegisterQuery>();
        services.AddSingleton<IBudgetService, BudgetService>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<IGoalService, GoalService>();
        services.AddKeelFileImportParsers();
        services.AddSingleton<IImportCategorizationHook, NoOpImportCategorizationHook>();
        services.AddSingleton<IImportService, ImportService>();
        services.AddSingleton<IImportSettingsStore, ImportSettingsStore>();
        services.TryAddSingleton<ICategorizationEngine, CategorizationEngine>();
        services.AddSingleton<IRuleService, RuleService>();
        services.AddSingleton<ILearnerService, LearnerService>();
        services.AddSingleton<ICategorizationService, CategorizationService>();
        services.AddSingleton<IImportCategorizationHook, RulesImportCategorizationHook>();
        services.AddSingleton<Keel.Infrastructure.Alerts.AlertService>();
        services.AddSingleton<Keel.Application.Alerts.IAlertService>(sp => sp.GetRequiredService<Keel.Infrastructure.Alerts.AlertService>());
        services.AddSingleton<Keel.Application.Recurring.IRecurringService, Keel.Infrastructure.Recurring.RecurringService>();
        services.AddSingleton<Keel.Application.Scheduling.IScheduledTransactionService, Keel.Infrastructure.Scheduling.ScheduledTransactionService>();
        services.AddSingleton<Keel.Infrastructure.Forecast.ForecastService>();
        services.AddSingleton<Keel.Application.Forecast.IForecastService>(sp => sp.GetRequiredService<Keel.Infrastructure.Forecast.ForecastService>());
        services.AddKeelSync();
        services.AddSingleton<Keel.Application.Backup.IBackupService, BackupService>();
        services.AddSingleton<IDataFileMaintenance, DataFileMaintenance>();
        services.AddSingleton<Keel.Application.Setup.ISetupProgressService, Keel.Infrastructure.Setup.SetupProgressService>();
        services.AddSingleton<Keel.Application.Tags.ITagService, Keel.Infrastructure.Tags.TagService>();
        services.AddSingleton<Keel.Application.Attachments.IAttachmentService, Keel.Infrastructure.Attachments.AttachmentService>();
        return services;
    }
}
