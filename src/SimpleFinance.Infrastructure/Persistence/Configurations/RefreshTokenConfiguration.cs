using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.HasKey(rt => rt.Id);

        // SHA-256 hex digest is always 64 chars; a hash must never collide across rows.
        builder.Property(rt => rt.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(rt => rt.TokenHash)
            .IsUnique();

        // Frequent lookup: "all refresh tokens for user X" — non-unique, a user has many.
        builder.HasIndex(rt => rt.UserId);

        // Frequent lookup during reuse-detection cascade: "all tokens in family X".
        builder.HasIndex(rt => rt.FamilyId);
    }
}
