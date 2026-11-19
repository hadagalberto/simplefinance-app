using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class CreditCardDetailConfiguration : IEntityTypeConfiguration<CreditCardDetail>
{
    public void Configure(EntityTypeBuilder<CreditCardDetail> builder)
    {
        builder.ToTable("CreditCardDetails");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Brand)
            .HasMaxLength(128);

        builder.Property(c => c.Level)
            .HasMaxLength(128);

        builder.Property(c => c.BrandAdditionalInfo)
            .HasMaxLength(256);

        builder.Property(c => c.HolderType)
            .HasMaxLength(32);

        // 1:1 child of Account.
        builder.HasIndex(c => c.AccountId)
            .IsUnique();
    }
}
