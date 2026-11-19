using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> builder)
    {
        builder.ToTable("Connections");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.PluggyItemId)
            .HasMaxLength(64)
            .IsRequired();

        // PLG-04/SYNC-04 backstop: one local Connection per Pluggy Item.
        builder.HasIndex(c => c.PluggyItemId)
            .IsUnique();

        builder.Property(c => c.ConnectorName)
            .HasMaxLength(256);

        builder.Property(c => c.InstitutionName)
            .HasMaxLength(256);

        builder.Property(c => c.ImageUrl)
            .HasMaxLength(1024);

        // Free-text per RESEARCH.md — Pluggy has ~24 executionStatus values that can grow
        // without a code migration.
        builder.Property(c => c.ExecutionStatus)
            .HasMaxLength(64);

        builder.Property(c => c.StatusDetail)
            .HasMaxLength(512);

        builder.Property(c => c.ErrorCode)
            .HasMaxLength(64);

        builder.Property(c => c.ErrorMessage)
            .HasMaxLength(1024);

        builder.HasIndex(c => c.UserId);
    }
}
