using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(c => c.Icon)
            .HasMaxLength(64);

        builder.Property(c => c.Color)
            .HasMaxLength(16);

        builder.Property(c => c.IsSystem)
            .HasDefaultValue(false);

        builder.Property(c => c.IsHidden)
            .HasDefaultValue(false);

        builder.HasIndex(c => c.UserId);
        builder.HasIndex(c => c.ParentCategoryId);
    }
}
