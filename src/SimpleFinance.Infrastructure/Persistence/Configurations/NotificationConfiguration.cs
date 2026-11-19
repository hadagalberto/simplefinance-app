using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Title)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(n => n.Message)
            .HasMaxLength(2000)
            .IsRequired();

        // Unread-count query.
        builder.HasIndex(n => new { n.UserId, n.IsRead });

        // Latest-N-notifications query.
        builder.HasIndex(n => new { n.UserId, n.CreatedAt });
    }
}
