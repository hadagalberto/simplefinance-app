using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class CategoryMappingConfiguration : IEntityTypeConfiguration<CategoryMapping>
{
    public void Configure(EntityTypeBuilder<CategoryMapping> builder)
    {
        builder.ToTable("CategoryMappings");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.PluggyCategoryId)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(c => c.PluggyCategoryId)
            .IsUnique();

        builder.Property(c => c.PluggyCategoryName)
            .HasMaxLength(256);

        builder.HasIndex(c => c.CategoryId);
    }
}
