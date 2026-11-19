using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF config for <see cref="AiInsight"/> (Phase 18, AI-INS-01/03/04). The
/// <see cref="AiInsight.DataJson"/> is stored as a native Postgres <c>jsonb</c> column (the
/// deterministic numbers that ground the displayed text). Two indexes back the two hot paths:
/// <c>(UserId, Status)</c> for the insights list (<c>GET /api/ai/insights?status=new</c>) and the
/// UNIQUE <c>(UserId, DedupKey)</c> — the anti-spam / fire-once spine that makes job re-runs idempotent
/// AND makes "dismissed stays dismissed" a single existence check (no separate fire-log table).
/// Auto-discovered via <c>ApplyConfigurationsFromAssembly</c> — no manual registration.
/// </summary>
public class AiInsightConfiguration : IEntityTypeConfiguration<AiInsight>
{
    public void Configure(EntityTypeBuilder<AiInsight> builder)
    {
        builder.ToTable("AiInsights");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.DataJson)
            .HasColumnType("jsonb");

        builder.Property(x => x.Title)
            .HasMaxLength(200);

        builder.Property(x => x.Body)
            .HasMaxLength(1000);

        builder.Property(x => x.DedupKey)
            .HasMaxLength(200);

        // Insights-list query (GET /api/ai/insights?status=new).
        builder.HasIndex(x => new { x.UserId, x.Status });

        // Anti-spam / fire-once spine: idempotent job re-runs + dismissed-stays-dismissed in one check.
        builder.HasIndex(x => new { x.UserId, x.DedupKey })
            .IsUnique();
    }
}
