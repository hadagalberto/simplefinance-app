using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class AiUsageLogConfiguration : IEntityTypeConfiguration<AiUsageLog>
{
    public void Configure(EntityTypeBuilder<AiUsageLog> builder)
    {
        builder.ToTable("AiUsageLogs");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Model)
            .HasMaxLength(64);

        // Sub-cent per-call costs (fractions of a cent) must not round to zero under the global
        // decimal(18,2) convention, or the monthly budget sum could never trip (RESEARCH Pitfall 6).
        builder.Property(x => x.EstimatedCostUsd)
            .HasPrecision(18, 6);

        // Hot query for the 15-04 budget guard: SUM(EstimatedCostUsd) for a user over the current month.
        builder.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}
