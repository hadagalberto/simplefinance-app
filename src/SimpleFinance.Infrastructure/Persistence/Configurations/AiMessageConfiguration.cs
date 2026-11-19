using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF config for <see cref="AiMessage"/> (17-01, AI-CHAT-01). The tool-call/result blobs are stored as
/// native Postgres <c>jsonb</c> columns; a <c>(UserId, ConversationId)</c> index backs the per-thread
/// message query (load a conversation with its messages, UserId-scoped).
/// </summary>
public class AiMessageConfiguration : IEntityTypeConfiguration<AiMessage>
{
    public void Configure(EntityTypeBuilder<AiMessage> builder)
    {
        builder.ToTable("AiMessages");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ToolCallsJson)
            .HasColumnType("jsonb");

        builder.Property(x => x.ToolResultJson)
            .HasColumnType("jsonb");

        builder.Property(x => x.ToolName)
            .HasMaxLength(200);

        // Per-thread message query (load conversation + messages, UserId-scoped).
        builder.HasIndex(x => new { x.UserId, x.ConversationId });
    }
}
