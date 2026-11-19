using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF config for <see cref="AiPendingAction"/> (17-01, AI-CHAT-04). The <see cref="AiPendingAction.Payload"/>
/// is stored as a native Postgres <c>jsonb</c> column (the proposed-change blob written by a write-tool,
/// read by the confirm endpoint in 17-06). A <c>(UserId, Status)</c> index backs the pending-actions
/// list, exactly like <see cref="AiSuggestionConfiguration"/>.
/// </summary>
public class AiPendingActionConfiguration : IEntityTypeConfiguration<AiPendingAction>
{
    public void Configure(EntityTypeBuilder<AiPendingAction> builder)
    {
        builder.ToTable("AiPendingActions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Payload)
            .HasColumnType("jsonb");

        // Pending-actions list query (UserId-scoped, filter by Status).
        builder.HasIndex(x => new { x.UserId, x.Status });
    }
}
