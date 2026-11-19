using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> builder)
    {
        builder.ToTable("AlertRules");

        builder.HasKey(r => r.Id);

        // Non-unique — a user can have multiple rules of the same type scoped to different
        // categories/accounts.
        builder.HasIndex(r => new { r.UserId, r.Type });
    }
}
