using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Keel.Infrastructure.Persistence.Configurations;

internal sealed class CategoryGroupConfiguration : IEntityTypeConfiguration<CategoryGroup>
{
    public void Configure(EntityTypeBuilder<CategoryGroup> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).IsRequired().HasMaxLength(200);
        builder.HasMany(g => g.Categories).WithOne(c => c.Group).HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Restrict);

        builder.HasData(
            new CategoryGroup { Id = SystemIds.InflowGroup, Name = "Inflow", SortOrder = 0, IsSystem = true },
            new CategoryGroup { Id = SystemIds.CreditCardPaymentsGroup, Name = "Credit Card Payments", SortOrder = 1, IsSystem = true });
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
        builder.Property(c => c.Notes).HasMaxLength(4000);
        builder.HasOne<Account>().WithMany().HasForeignKey(c => c.LinkedAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.GroupId, c.SortOrder });

        builder.HasData(new Category
        {
            Id = SystemIds.ReadyToAssignCategory,
            GroupId = SystemIds.InflowGroup,
            Name = "Ready to Assign",
            SortOrder = 0,
            IsSystem = true,
            FlexKind = FlexKind.Unset,
        });
    }
}

internal sealed class BudgetAssignmentConfiguration : IEntityTypeConfiguration<BudgetAssignment>
{
    public void Configure(EntityTypeBuilder<BudgetAssignment> builder)
    {
        builder.HasKey(b => new { b.CategoryId, b.Month });
        builder.HasOne<Category>().WithMany().HasForeignKey(b => b.CategoryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(b => b.Month);
    }
}

internal sealed class TargetConfiguration : IEntityTypeConfiguration<Target>
{
    public void Configure(EntityTypeBuilder<Target> builder)
    {
        builder.HasKey(t => t.CategoryId);
        builder.HasOne<Category>().WithOne().HasForeignKey<Target>(t => t.CategoryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.LinkedAccountId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PayeeConfiguration : IEntityTypeConfiguration<Payee>
{
    public void Configure(EntityTypeBuilder<Payee> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.NormalizedName).IsRequired().HasMaxLength(200);
        builder.HasIndex(p => p.NormalizedName).IsUnique();
        builder.HasOne<Category>().WithMany().HasForeignKey(p => p.DefaultCategoryId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Account>().WithMany().HasForeignKey(p => p.IsTransferPayeeForAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(200);
        builder.Property(r => r.ConditionsJson).IsRequired();
        builder.Property(r => r.ActionsJson).IsRequired();
        builder.HasIndex(r => r.SortOrder);
    }
}
