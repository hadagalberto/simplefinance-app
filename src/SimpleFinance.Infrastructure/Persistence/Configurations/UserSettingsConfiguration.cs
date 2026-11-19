using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class UserSettingsConfiguration : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> builder)
    {
        builder.ToTable("UserSettings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Timezone)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.BaseCurrency)
            .HasMaxLength(8)
            .IsRequired();

        builder.Property(s => s.SyncCronExpression)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.DefaultTheme)
            .HasMaxLength(16)
            .IsRequired();

        // --- AI settings string columns (15-01). AiMonthlyBudgetUsd inherits the global
        // decimal(18,2) convention — a USD budget needs no extra scale. ---
        builder.Property(s => s.AiChatModel)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.AiSummaryModel)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.AiLanguage)
            .HasMaxLength(16)
            .IsRequired();

        // Encrypted-at-rest provider keys (ciphertext only). Nullable — null ⇒ env fallback. The
        // Base64(nonce||ciphertext||tag) blob is short but unbounded; use a generous cap.
        builder.Property(s => s.DeepSeekApiKeyEncrypted)
            .HasMaxLength(512);

        builder.Property(s => s.OpenAiApiKeyEncrypted)
            .HasMaxLength(512);

        builder.Property(s => s.AnthropicApiKeyEncrypted)
            .HasMaxLength(512);

        builder.Property(s => s.OpenRouterApiKeyEncrypted)
            .HasMaxLength(512);

        // One settings row per user.
        builder.HasIndex(s => s.UserId)
            .IsUnique();
    }
}
