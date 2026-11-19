using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class RecurringTransactionConfiguration : IEntityTypeConfiguration<RecurringTransaction>
{
    public void Configure(EntityTypeBuilder<RecurringTransaction> builder)
    {
        builder.ToTable("RecurringTransactions");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.NormalizedMerchant)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.DisplayName)
            .HasMaxLength(256)
            .IsRequired();

        // The single most load-bearing constraint in this plan — the exact key a later
        // plan's idempotent upsert matches on (07-CONTEXT.md: "Idempotent upsert... update
        // on re-run, don't duplicate").
        builder.HasIndex(r => new { r.UserId, r.NormalizedMerchant })
            .IsUnique();

        builder.HasIndex(r => r.CategoryId);
    }
}
