using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("WebhookEvents");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.EventId)
            .HasMaxLength(64)
            .IsRequired();

        // The dedup backstop for SYNC-01's replay-safety requirement.
        builder.HasIndex(w => w.EventId)
            .IsUnique();

        builder.Property(w => w.EventType)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(w => w.PluggyItemId)
            .HasMaxLength(64);

        builder.Property(w => w.PayloadJson)
            .IsRequired();

        builder.Property(w => w.Error)
            .HasMaxLength(1024);

        builder.HasIndex(w => w.PluggyItemId);
    }
}
