using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Persistence;

/// <summary>
/// The EF Core model of a <c>.keel</c> budget file (PRD 6.2). Conventions:
/// amounts are <c>long</c> minor units (INTEGER), <see cref="DateOnly"/> is ISO
/// <c>yyyy-MM-dd</c> TEXT, timestamps are UTC, enums are stored by name.
/// </summary>
public class KeelDbContext(DbContextOptions<KeelDbContext> options) : DbContext(options)
{
    /// <summary>Profiles.</summary>
    public DbSet<Profile> Profiles => Set<Profile>();

    /// <summary>Accounts.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>Sync connections.</summary>
    public DbSet<SyncConnection> SyncConnections => Set<SyncConnection>();

    /// <summary>Category groups.</summary>
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();

    /// <summary>Categories.</summary>
    public DbSet<Category> Categories => Set<Category>();

    /// <summary>Transactions (soft-deleted rows are filtered out).</summary>
    public DbSet<Transaction> Transactions => Set<Transaction>();

    /// <summary>Transaction splits.</summary>
    public DbSet<TransactionSplit> TransactionSplits => Set<TransactionSplit>();

    /// <summary>Payees.</summary>
    public DbSet<Payee> Payees => Set<Payee>();

    /// <summary>Budget assignments.</summary>
    public DbSet<BudgetAssignment> BudgetAssignments => Set<BudgetAssignment>();

    /// <summary>Category targets.</summary>
    public DbSet<Target> Targets => Set<Target>();

    /// <summary>Scheduled transactions.</summary>
    public DbSet<ScheduledTransaction> ScheduledTransactions => Set<ScheduledTransaction>();

    /// <summary>Recurring items.</summary>
    public DbSet<RecurringItem> RecurringItems => Set<RecurringItem>();

    /// <summary>Rules.</summary>
    public DbSet<Rule> Rules => Set<Rule>();

    /// <summary>Balance snapshots.</summary>
    public DbSet<BalanceSnapshot> BalanceSnapshots => Set<BalanceSnapshot>();

    /// <summary>Reconciliations.</summary>
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();

    /// <summary>Tags.</summary>
    public DbSet<Tag> Tags => Set<Tag>();

    /// <summary>Transaction tags.</summary>
    public DbSet<TransactionTag> TransactionTags => Set<TransactionTag>();

    /// <summary>Attachments.</summary>
    public DbSet<Attachment> Attachments => Set<Attachment>();

    /// <summary>Alerts.</summary>
    public DbSet<Alert> Alerts => Set<Alert>();

    /// <summary>Data-file settings.</summary>
    public DbSet<Setting> Settings => Set<Setting>();

    /// <summary>Audit events.</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Enums by name: readable in the user's file and robust to reordering.
        configurationBuilder.Properties<AccountType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TransactionStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TransactionSource>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<SyncProvider>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<SyncStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TargetType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<FlexKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RecurrenceCadence>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RecurringStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AlertKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<BalanceSource>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AuditEventKind>().HaveConversion<string>().HaveMaxLength(32);

        // Timestamps are UTC; SQLite returns them as Unspecified, so mark them on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KeelDbContext).Assembly);
    }
}
