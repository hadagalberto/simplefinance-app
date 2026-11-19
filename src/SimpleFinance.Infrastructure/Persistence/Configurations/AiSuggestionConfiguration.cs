using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF config for <see cref="AiSuggestion"/> (16-04, AI-CAT-03/05/06). The <see cref="AiSuggestion.SuggestedPayload"/>
/// is stored as a native Postgres <c>jsonb</c> column (a small <c>{"categoryId":"…"}</c> blob written
/// by the job / read by 16-05's endpoints). Two indexes back the two hot queries:
/// <c>(UserId, Status)</c> for the pending-suggestions list (16-05's GET) and
/// <c>(UserId, TransactionId)</c> for the job's per-transaction idempotency guard (never double-queue
/// a Pending suggestion for the same transaction).
/// </summary>
public class AiSuggestionConfiguration : IEntityTypeConfiguration<AiSuggestion>
{
    public void Configure(EntityTypeBuilder<AiSuggestion> builder)
    {
        builder.ToTable("AiSuggestions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SuggestedPayload)
            .HasColumnType("jsonb");

        builder.Property(x => x.Rationale)
            .HasMaxLength(1000);

        // Pending-list query (16-05 GET /api/ai/suggestions?status=pending).
        builder.HasIndex(x => new { x.UserId, x.Status });

        // Per-transaction idempotency guard in AiCategorizationJob (skip if a Pending suggestion exists).
        builder.HasIndex(x => new { x.UserId, x.TransactionId });
    }
}
