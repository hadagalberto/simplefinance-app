using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF config for <see cref="AiConversation"/> (17-01, AI-CHAT-01). Backs the recency-ordered
/// conversation list with a <c>(UserId, UpdatedAt)</c> index — every list query is UserId-scoped and
/// ordered by most-recent activity (the append path bumps UpdatedAt).
/// </summary>
public class AiConversationConfiguration : IEntityTypeConfiguration<AiConversation>
{
    public void Configure(EntityTypeBuilder<AiConversation> builder)
    {
        builder.ToTable("AiConversations");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Title)
            .HasMaxLength(200);

        // Recency-ordered conversation list (UserId-scoped, newest activity first).
        builder.HasIndex(x => new { x.UserId, x.UpdatedAt });
    }
}
