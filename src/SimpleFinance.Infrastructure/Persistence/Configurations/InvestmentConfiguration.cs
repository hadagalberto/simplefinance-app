using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class InvestmentConfiguration : IEntityTypeConfiguration<Investment>
{
    public void Configure(EntityTypeBuilder<Investment> builder)
    {
        builder.ToTable("Investments");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.PluggyInvestmentId)
            .HasMaxLength(64)
            .IsRequired();

        // SYNC-04 idempotent upsert key backstop.
        builder.HasIndex(i => new { i.ConnectionId, i.PluggyInvestmentId })
            .IsUnique();

        builder.Property(i => i.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(i => i.Subtype)
            .HasMaxLength(64);

        builder.Property(i => i.Institution)
            .HasMaxLength(256);

        builder.Property(i => i.IssuerName)
            .HasMaxLength(256);

        builder.Property(i => i.CurrencyCode)
            .HasMaxLength(8);

        // PITFALLS.md #7 / threat T-02-02: the DbContext's global HavePrecision(18, 2)
        // convention would silently truncate crypto quantities/rates — override explicitly.
        builder.Property(i => i.Quantity)
            .HasPrecision(18, 8);

        builder.Property(i => i.LastMonthRate)
            .HasPrecision(9, 6);

        builder.Property(i => i.AnnualRate)
            .HasPrecision(9, 6);

        builder.HasIndex(i => i.UserId);
        builder.HasIndex(i => i.ConnectionId);
    }
}
