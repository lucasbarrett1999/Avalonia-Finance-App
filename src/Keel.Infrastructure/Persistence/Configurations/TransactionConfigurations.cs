using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Keel.Infrastructure.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Ignore(t => t.IsSplit);
        builder.Ignore(t => t.SplitsBalance);
        builder.Property(t => t.PayeeRaw).IsRequired().HasMaxLength(500);
        builder.Property(t => t.Memo).HasMaxLength(2000);
        builder.Property(t => t.ProviderTransactionId).HasMaxLength(200);
        builder.Property(t => t.ProviderPendingId).HasMaxLength(200);
        builder.Property(t => t.ImportFingerprint).HasMaxLength(64);

        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.TransferAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payee>().WithMany().HasForeignKey(t => t.PayeeId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Category>().WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ScheduledTransaction>().WithMany().HasForeignKey(t => t.ScheduledFromId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(t => t.Splits).WithOne(s => s.Transaction).HasForeignKey(s => s.TransactionId).OnDelete(DeleteBehavior.Cascade);

        // Soft delete: IsDeleted rows are excluded from every query and all math.
        builder.HasQueryFilter(t => !t.IsDeleted);

        // Required indexes (PRD 6.2).
        builder.HasIndex(t => new { t.AccountId, t.Date });
        builder.HasIndex(t => t.Date);
        builder.HasIndex(t => new { t.CategoryId, t.Date });
        builder.HasIndex(t => t.ImportFingerprint);
        builder.HasIndex(t => t.ProviderTransactionId);
        builder.HasIndex(t => t.PayeeId);
        builder.HasIndex(t => t.TransferPairId);

        // Covering indexes for the paged register (M1): ledger order (Date, Id) per account and
        // across accounts, with the columns running balances and header sums read.
        builder.HasIndex(t => new { t.AccountId, t.Date, t.Id, t.IsDeleted, t.Amount, t.Status })
            .HasDatabaseName("IX_Transactions_Register_Account");
        builder.HasIndex(t => new { t.Date, t.Id, t.IsDeleted, t.Amount, t.Status })
            .HasDatabaseName("IX_Transactions_Register_All");
    }
}

internal sealed class TransactionSplitConfiguration : IEntityTypeConfiguration<TransactionSplit>
{
    public void Configure(EntityTypeBuilder<TransactionSplit> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Memo).HasMaxLength(2000);
        builder.HasOne<Category>().WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Account>().WithMany().HasForeignKey(s => s.TransferAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.CategoryId);

        // Splits of a soft-deleted transaction are excluded too.
        builder.HasQueryFilter(s => !s.Transaction!.IsDeleted);

        // sum(splits) == parent amount is enforced by triggers and a deferred constraint created
        // in the initial migration (see docs/decisions/0003-split-sum-constraint-via-triggers.md).
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.HasIndex(t => t.Name).IsUnique();
    }
}

internal sealed class TransactionTagConfiguration : IEntityTypeConfiguration<TransactionTag>
{
    public void Configure(EntityTypeBuilder<TransactionTag> builder)
    {
        builder.HasKey(t => new { t.TransactionId, t.TagId });
        builder.HasOne(t => t.Transaction).WithMany().HasForeignKey(t => t.TransactionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tag>().WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.TagId);
        builder.HasQueryFilter(t => !t.Transaction!.IsDeleted);
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(260);
        builder.Property(a => a.Sha256).IsRequired().HasMaxLength(64);
        builder.Property(a => a.MimeType).IsRequired().HasMaxLength(100);
        builder.HasOne(a => a.Transaction).WithMany().HasForeignKey(a => a.TransactionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.Sha256);
        builder.HasQueryFilter(a => !a.Transaction!.IsDeleted);
    }
}

internal sealed class ScheduledTransactionConfiguration : IEntityTypeConfiguration<ScheduledTransaction>
{
    public void Configure(EntityTypeBuilder<ScheduledTransaction> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Memo).HasMaxLength(2000);
        builder.Property(s => s.RecurrenceRule).IsRequired().HasMaxLength(500);
        builder.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Account>().WithMany().HasForeignKey(s => s.TransferAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payee>().WithMany().HasForeignKey(s => s.PayeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Category>().WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.NextDate);
    }
}

internal sealed class RecurringItemConfiguration : IEntityTypeConfiguration<RecurringItem>
{
    public void Configure(EntityTypeBuilder<RecurringItem> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasOne<Payee>().WithMany().HasForeignKey(r => r.PayeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Category>().WithMany().HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ScheduledTransaction>().WithMany().HasForeignKey(r => r.ScheduledTransactionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(r => new { r.PayeeId, r.AccountId });
        builder.HasIndex(r => r.NextExpectedDate);
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.PayloadJson).IsRequired();
        builder.HasOne<RecurringItem>().WithMany().HasForeignKey(a => a.RecurringItemId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Transaction>().WithMany().HasForeignKey(a => a.TransactionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(a => a.CreatedAt);
    }
}

internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(200);
        builder.Property(s => s.ValueJson).IsRequired();
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();
        builder.Property(a => a.EntityType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.EntityId).IsRequired().HasMaxLength(100);
        builder.HasIndex(a => a.At);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
