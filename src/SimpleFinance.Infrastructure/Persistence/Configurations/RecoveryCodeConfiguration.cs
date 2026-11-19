using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("RecoveryCodes");

        builder.HasKey(rc => rc.Id);

        // SHA-256 hex digest is always 64 chars.
        builder.Property(rc => rc.CodeHash)
            .HasMaxLength(64)
            .IsRequired();

        // Frequent lookup: "all recovery codes for user X".
        builder.HasIndex(rc => rc.UserId);

        // A given hash must never collide across rows for the same user.
        builder.HasIndex(rc => new { rc.UserId, rc.CodeHash })
            .IsUnique();
    }
}
