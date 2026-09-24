using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Keel.Infrastructure.Persistence.Configurations;

internal sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.HasData(new Profile { Id = SystemIds.DefaultProfile, Name = "Me", IsDefault = true });
    }
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Ignore(a => a.Group);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(200);
        builder.Property(a => a.Currency).IsRequired().HasMaxLength(3);
        builder.Property(a => a.Notes).HasMaxLength(4000);
        builder.Property(a => a.ProviderAccountId).HasMaxLength(200);

        builder.HasOne<Profile>().WithMany().HasForeignKey(a => a.OwnerProfileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<SyncConnection>().WithMany().HasForeignKey(a => a.SyncConnectionId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => new { a.SyncConnectionId, a.ProviderAccountId });
    }
}

internal sealed class SyncConnectionConfiguration : IEntityTypeConfiguration<SyncConnection>
{
    public void Configure(EntityTypeBuilder<SyncConnection> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.InstitutionName).IsRequired().HasMaxLength(200);
        builder.Property(c => c.ExternalItemId).IsRequired().HasMaxLength(200);
        builder.Property(c => c.SecretRef).IsRequired().HasMaxLength(200);
        builder.Property(c => c.LastError).HasMaxLength(2000);
    }
}

internal sealed class BalanceSnapshotConfiguration : IEntityTypeConfiguration<BalanceSnapshot>
{
    public void Configure(EntityTypeBuilder<BalanceSnapshot> builder)
    {
        // The composite key doubles as the required BalanceSnapshot(AccountId, Date) index.
        builder.HasKey(s => new { s.AccountId, s.Date });
        builder.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReconciliationConfiguration : IEntityTypeConfiguration<Reconciliation>
{
    public void Configure(EntityTypeBuilder<Reconciliation> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.AccountId, r.StatementDate });
    }
}
