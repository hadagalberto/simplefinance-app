using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class AlertFireLogConfiguration : IEntityTypeConfiguration<AlertFireLog>
{
    public void Configure(EntityTypeBuilder<AlertFireLog> builder)
    {
        builder.ToTable("AlertFireLogs");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.TargetKey)
            .HasMaxLength(256)
            .IsRequired();

        // Load-bearing — the exact constraint TryRecordFireAsync's existence-check relies on.
        builder.HasIndex(f => new { f.AlertRuleId, f.TargetKey })
            .IsUnique();
    }
}
