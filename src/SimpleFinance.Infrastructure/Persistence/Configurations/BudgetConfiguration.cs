using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable("Budgets");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Year)
            .IsRequired();

        builder.Property(b => b.Month)
            .IsRequired();

        builder.Property(b => b.LimitAmount)
            .IsRequired();

        // At most one limit per (user, category, year, month). Postgres treats every NULL as
        // distinct in a standard unique index, so without the "CategoryId IS NOT NULL" filter
        // this index would silently allow duplicate rows once CategoryId is null — the filter
        // scopes this index to per-category rows only.
        builder.HasIndex(b => new { b.UserId, b.CategoryId, b.Year, b.Month })
            .IsUnique()
            .HasFilter("\"CategoryId\" IS NOT NULL");

        // At most one global cap per (user, year, month). A plain unique index across all four
        // columns would NOT enforce this — Postgres allows unlimited rows with CategoryId = NULL
        // in a standard unique index, so this filtered index is load-bearing, not stylistic.
        builder.HasIndex(b => new { b.UserId, b.Year, b.Month })
            .IsUnique()
            .HasFilter("\"CategoryId\" IS NULL");
    }
}
