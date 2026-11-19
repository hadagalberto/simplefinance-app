using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class NetWorthSnapshotConfiguration : IEntityTypeConfiguration<NetWorthSnapshot>
{
    public void Configure(EntityTypeBuilder<NetWorthSnapshot> builder)
    {
        builder.ToTable("NetWorthSnapshots");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Date)
            .IsRequired();

        builder.Property(s => s.TotalAssets)
            .IsRequired();

        builder.Property(s => s.TotalLiabilities)
            .IsRequired();

        builder.Property(s => s.NetWorth)
            .IsRequired();

        builder.Property(s => s.AccountsBalance)
            .IsRequired();

        builder.Property(s => s.InvestmentsBalance)
            .IsRequired();

        builder.Property(s => s.CreditCardDebt)
            .IsRequired();

        // Date is never null, so no partial filter is needed here (unlike BudgetConfiguration).
        builder.HasIndex(s => new { s.UserId, s.Date })
            .IsUnique();
    }
}
