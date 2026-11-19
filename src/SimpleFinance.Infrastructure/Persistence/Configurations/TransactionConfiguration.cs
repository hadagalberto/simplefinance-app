using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("Transactions");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.PluggyTransactionId)
            .HasMaxLength(64)
            .IsRequired();

        // The single most important constraint in the whole phase — the hard backstop behind
        // SYNC-04's "never duplicates" requirement.
        builder.HasIndex(t => new { t.AccountId, t.PluggyTransactionId })
            .IsUnique();

        builder.Property(t => t.Description)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(t => t.DescriptionRaw)
            .HasMaxLength(512);

        builder.Property(t => t.CurrencyCode)
            .HasMaxLength(8)
            .IsRequired();

        builder.Property(t => t.MerchantName)
            .HasMaxLength(256);

        builder.Property(t => t.MerchantDocument)
            .HasMaxLength(32);

        builder.Property(t => t.PluggyCategory)
            .HasMaxLength(128);

        builder.Property(t => t.PluggyCategoryId)
            .HasMaxLength(64);

        builder.Property(t => t.BillId)
            .HasMaxLength(64);

        // AI-CAT-03: model name that produced the AI category (kNN embedding model or LLM chat
        // model). Matches the AiUsageLog.Model 64-char convention. CategoryConfidence needs no
        // explicit mapping — nullable double maps to double precision.
        builder.Property(t => t.CategoryModel)
            .HasMaxLength(64);

        builder.Property(t => t.IsTransfer)
            .HasDefaultValue(false);

        builder.Property(t => t.IsTransferManualOverride)
            .HasDefaultValue(false);

        builder.Property(t => t.IsHidden)
            .HasDefaultValue(false);

        builder.HasIndex(t => t.UserId);
        builder.HasIndex(t => t.AccountId);
        builder.HasIndex(t => t.CategoryId);
        builder.HasIndex(t => t.Date);
        builder.HasIndex(t => t.RecurringTransactionId);
    }
}
