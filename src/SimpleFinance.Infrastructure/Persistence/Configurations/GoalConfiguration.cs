using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class GoalConfiguration : IEntityTypeConfiguration<Goal>
{
    public void Configure(EntityTypeBuilder<Goal> builder)
    {
        builder.ToTable("Goals");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(g => g.Icon)
            .HasMaxLength(64);

        builder.Property(g => g.Color)
            .HasMaxLength(32);

        // No uniqueness constraint — a user may have multiple goals with the same name.
        builder.HasIndex(g => g.UserId);
        builder.HasIndex(g => g.LinkedAccountId);
        builder.HasIndex(g => g.LinkedInvestmentId);
    }
}
