using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.PluggyAccountId)
            .HasMaxLength(64)
            .IsRequired();

        // SYNC-04 idempotent upsert key backstop.
        builder.HasIndex(a => new { a.ConnectionId, a.PluggyAccountId })
            .IsUnique();

        builder.Property(a => a.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(a => a.MarketingName)
            .HasMaxLength(256);

        builder.Property(a => a.NumberMasked)
            .HasMaxLength(64);

        builder.Property(a => a.Owner)
            .HasMaxLength(256);

        builder.Property(a => a.CurrencyCode)
            .HasMaxLength(8)
            .IsRequired();

        builder.Property(a => a.IsHidden)
            .HasDefaultValue(false);

        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.ConnectionId);
    }
}
