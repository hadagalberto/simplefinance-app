using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class SyncLogConfiguration : IEntityTypeConfiguration<SyncLog>
{
    public void Configure(EntityTypeBuilder<SyncLog> builder)
    {
        builder.ToTable("SyncLogs");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Error)
            .HasMaxLength(1024);

        // Frequent lookup pattern: "latest sync per connection".
        builder.HasIndex(s => s.ConnectionId);
    }
}
